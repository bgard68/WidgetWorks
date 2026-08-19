[← Handbook index](README.md) · [Project README](../../README.md)

# 5. Payments, checkout totals & testing credit cards

Payments sit behind one port, `IPaymentGateway`, so the checkout flow never knows which
provider is in use:

```csharp
public interface IPaymentGateway
{
    string Name { get; }
    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct);
}
```

The adapter is chosen by config: `Payments:Provider` = `Mock` (default) or `Stripe`.

## How checkout builds the total

`CheckoutHandler` (see [Architecture](02-architecture.md)) recomputes every number
**server-side** — the client's totals are never trusted:

```
subtotal (sum of line items)  →  + shipping  →  + sales tax  →  = total
```

1. **Re-prices** — subtotal from the cart, shipping from `IShippingCalculator`, sales tax
   from `ITaxCalculator` (below).
2. **Reserves stock + persists a pending order atomically** (a Dapper transaction).
3. **Charges** via `IPaymentGateway.ChargeAsync` for the server-computed total.
4. **Finalizes on the outcome** — three results:
   - **Succeeded** (synchronous, e.g. a card): mark Paid, clear the cart, email a receipt.
   - **Declined**: release the reservation and mark PaymentFailed.
   - **Pending** (asynchronous, e.g. BNPL/redirect): keep the reservation, park the order in
     **AwaitingPayment**, and wait for a provider **webhook** to settle it (see below).

No card numbers ever touch the app or the database — only a payment **token** and, after a
charge, the gateway's reference id. That keeps the app out of PCI scope.

## Sales tax — how it's calculated, and what's covered

### One rule, applied server-side

`StateSalesTaxCalculator` needs exactly two inputs: the **destination state** from the
shipping address, and the **subtotal**.

```
taxable  = subtotal                            ← shipping is NOT taxed in this model
rate     = rateTable[trim(upper(stateCode))]   ← unlisted, unknown or blank → 0
tax      = round(taxable × rate, 2, AwayFromZero)
total    = subtotal + shipping + tax
```

The state code is normalized before lookup (`" ca "` → `"CA"`), and the calculator returns
`TaxLine(StateCode, Rate, Amount)` where **`Rate` is a fraction** — `0.0725` means 7.25%.
Rounding is half-**away-from-zero**, the retail convention, rather than .NET's default
banker's rounding: `$6.525` bills as `$6.53`, not `$6.52`.

None of it trusts the browser. `CheckoutHandler` re-reads unit prices from the database and
recomputes the tax at the moment the order is placed, whatever the client displayed.

### Worked example

Three items totalling **$89.97**, shipped Standard:

| | to **California** | to **Oregon** | to **`""`/unknown** |
|---|---|---|---|
| Subtotal | $89.97 | $89.97 | $89.97 |
| Shipping | $0.00 *(free ≥ $75)* | $0.00 | $0.00 |
| Tax rate | `0.0725` | `0.0000` | `0.0000` |
| Tax | **$6.52** — `round(89.97 × 0.0725)` = `round(6.5228…)` | **$0.00** | **$0.00** |
| **Total** | **$96.49** | **$89.97** | **$89.97** |

Switch that same order to **Express** and it becomes `89.97 + 22.99 + 6.52 = $119.48` —
the shipping charge rises, the tax does not, because shipping isn't in the taxable base.

### Who pays it, and what's covered

- **The buyer pays it.** Tax is *added* to the order total; the store never absorbs,
  discounts, or nets it out. There is no exemption, resale-certificate, or tax-credit
  concept in this model.
- **Nothing is remitted.** Payments run against the mock gateway (or Stripe **test** mode),
  so no money — and therefore no tax — actually moves. The figure exists to exercise the
  pricing path, not to satisfy a filing obligation.
- **Coverage is all 50 states + DC**, at the **state base rate only**. Five states levy no
  state sales tax and correctly resolve to $0 — **AK, DE, MT, NH, OR**. Anything outside
  the table (a Canadian province, a typo, an empty string) resolves to **0%** rather than
  failing the order.
- **The order snapshots what it charged** — `tax_state`, `tax_rate` and `tax` are written
  onto the order row, so the exact rate applied is preserved on that order forever even if
  the table changes later.

### The rate table

Compiled in as of **`EffectiveOn` = 2025-07-01** (state base rates, as decimal fractions in
code — shown here as percentages):

| State | Rate | State | Rate | State | Rate | State | Rate |
|---|---:|---|---:|---|---:|---|---:|
| AK | 0% | ID | 6% | MT | 0% | RI | 7% |
| AL | 4% | IL | 6.25% | NC | 4.75% | SC | 6% |
| AR | 6.5% | IN | 7% | ND | 5% | SD | 4.2% |
| AZ | 5.6% | KS | 6.5% | NE | 5.5% | TN | 7% |
| CA | 7.25% | KY | 6% | NH | 0% | TX | 6.25% |
| CO | 2.9% | LA | 4.45% | NJ | 6.625% | UT | 6.1% |
| CT | 6.35% | MA | 6.25% | NM | 4.875% | VA | 5.3% |
| DC | 6% | MD | 6% | NV | 6.85% | VT | 6% |
| DE | 0% | ME | 5.5% | NY | 4% | WA | 6.5% |
| FL | 6% | MI | 6% | OH | 5.75% | WI | 5% |
| GA | 4% | MN | 6.875% | OK | 4.5% | WV | 6% |
| HI | 4% | MO | 4.225% | OR | 0% | WY | 4% |
| IA | 6% | MS | 7% | PA | 6% | | |

**Deliberate simplification:** real US sales tax is destination-based across thousands of
local/county/city jurisdictions, with product-category exemptions and economic-nexus rules.
This app uses a single **state-level base rate** as a documented approximation — enough to
demonstrate correct, server-side, snapshotted tax handling — with the seam in place so a
real engine replaces it without touching checkout.

### Where the rates come from — and when they update

Rates come from an `ITaxRateProvider`. The default, `StaticStateTaxRateProvider`, is an
**offline, versioned** table compiled into the app. Its freshness is made explicit by two
fields on the rate set:

- **`EffectiveOn`** — the date the rates are good as of (currently **2025-07-01**), and
- **`Source`** — a note on where the numbers came from.

It's registered as a **singleton, loaded once at process start**, so it does **not** poll or
refresh at runtime — the rates are fixed for the life of the deployment. "Updating the tax
table" therefore means one of two things:

1. **Edit the table and redeploy** — change the values in `StaticStateTaxRateProvider`
   (and bump `EffectiveOn`); the new rates take effect on the next deploy/restart.
2. **Swap the provider** — because tax sits behind the `ITaxRateProvider` / `ITaxCalculator`
   seam, you can drop in a live tax engine (**Avalara, TaxJar, Stripe Tax**) or a scheduled
   importer that pulls current rates from an authoritative dataset — **with zero changes to
   checkout**. A live engine is what actually "checks for updates" (per request or on its own
   schedule); the built-in table intentionally does not. This is the production path (ADR-022).

### Seeing the numbers without placing an order

`POST /checkout/quote` runs the **same** shipping and tax calculators without creating an
order, which is how the cart and checkout screens show a live breakdown as you pick a state
or a shipping method:

```json
{ "subtotal": 89.97, "shippingMethod": "Standard", "shipping": 0.00,
  "stateCode": "CA", "taxRate": 0.0725, "tax": 6.52, "total": 96.49,
  "itemCount": 3, "isEmpty": false }
```

`GET /checkout/tax-info` reports the table's provenance rather than any rate —
`{ effectiveOn, source, stateCount }` — so staleness is visible from outside the app.
`GET /checkout/shipping-methods` lists the methods the calculator accepts.

### Shipping, for completeness

`FlatRateShippingCalculator` is the other half of the total, and is tiered rather than flat
despite the name:

| Method | Charge |
|---|---|
| **Standard** | **free** when subtotal ≥ **$75**; otherwise **$6.99** + **$0.75** per item beyond the first |
| **Express** | **$19.99** + **$1.50** per item beyond the first (no free threshold) |

`itemCount` is the sum of quantities, not the number of distinct lines — one line of qty 2
counts as 2, so the surcharge applies. Anything other than `Express` normalizes to
`Standard`, and the result is rounded to 2dp away-from-zero.

## Asynchronous payments (BNPL / redirect) & webhooks

Not every method settles during the request. Buy-now-pay-later (Klarna, Affirm,
Afterpay) and other redirect methods authorize asynchronously: the shopper is sent off to
approve, and the provider tells you the result **later**, out of band, via a webhook. The
order model handles this with an explicit resting state:

```
placed ──► Pending ──► (charge)
                         ├─ Succeeded ─────────────► Paid
                         ├─ Declined ──────────────► PaymentFailed  (reservation released)
                         └─ Pending ──► AwaitingPayment
                                             │  provider webhook
                                             ├─ succeeded ────────► Paid           (+ receipt email)
                                             └─ failed ───────────► PaymentFailed  (reservation released)
```

Key properties:

- **AwaitingPayment holds the reservation.** Stock stays committed to the order while
  payment settles, so it can't be oversold; a failure releases it.
- **The order state machine still guards fulfillment.** Only a **Paid** order can be
  Shipped, so an admin can't ship something that hasn't settled — an `AwaitingPayment`
  order returns `400` on a status change.
- **Webhooks are verified, then normalized.** Each provider implements
  `IPaymentWebhookParser`, which verifies the payload's signature and maps it to a
  normalized `PaymentEvent(Provider, Reference, Type)`. `ConfirmPaymentHandler` looks the
  order up by `(provider, reference)` and transitions it.
- **Settlement is idempotent.** Providers retry webhooks, so a duplicate delivery — or an
  event for an order that already moved on — is a no-op that returns the current status.

### The webhook endpoint

```
POST /webhooks/payments/{provider}
```

`{provider}` selects the parser (`mock`, `stripe`). The raw body is read and handed to the
parser with the signature header (`Stripe-Signature`, or `X-Webhook-Signature` for the
mock). Responses: **404** unknown provider · **400** unverifiable/malformed · **200**
acknowledged (with the resulting status, or `ignored` when no order matches).

For **Stripe**, the parser verifies the `Stripe-Signature` header (HMAC-SHA256 of
`"{timestamp}.{payload}"` keyed by the `whsec_…` secret) and handles
`payment_intent.succeeded` / `payment_intent.payment_failed` / `payment_intent.canceled`.
Configure it with `Payments__Stripe__WebhookSecret=whsec_...` (secrets/env, never committed).

## Testing without charging a real card

You never need a real card (or even a Stripe account) to exercise checkout end to end.

### Mock gateway (default) — no account, no charge

`MockPaymentGateway` approves any positive charge and returns a synthetic reference,
**except**:

| Payment token | Result |
|---|---|
| anything (e.g. `tok_visa_ok`, `gpay_demo`) | Approved — order becomes **Paid** |
| a token containing `decline` (e.g. `card-decline`) | **Declined** — reservation released, order **PaymentFailed** |
| an async marker (`klarna…`, `bnpl…`, `async…`, `affirm…`, `afterpay…`) | **Pending** — order **AwaitingPayment**, settled by a webhook |
| amount ≤ 0 | Declined |

To settle a mock async order locally (no account, no signature needed by default):

```bash
curl -X POST http://localhost:8080/webhooks/payments/mock \
  -H 'Content-Type: application/json' \
  -d '{"reference":"<paymentReference from checkout>","outcome":"succeeded"}'
```

Use `"outcome":"failed"` for the failure path. The storefront's confirmation page has a demo
button that does exactly this. The smoke test exercises both paths plus the guardrails.

### Stripe test mode — real integration, still no money

Set `Payments:Provider=Stripe` and a **`sk_test_…`** key (via secrets/env), then use Stripe's
standard **test** instruments:

| Test PaymentMethod / card | Outcome |
|---|---|
| `pm_card_visa` (default when no token given) | Succeeds |
| `4242 4242 4242 4242` | Succeeds |
| `4000 0000 0000 0002` | Card declined |
| `4000 0000 0000 9995` | Insufficient funds |

A card charge returns `succeeded` → order Paid. A redirect/BNPL method returns
`requires_action`/`processing` → **AwaitingPayment**, settled later by the Stripe webhook.

## Going live (real payments)

Going live is the **same Stripe integration** with your own **live** keys — no code change:

1. Provide `Payments__Provider=Stripe`, `Payments__Stripe__SecretKey=sk_live_…`, and
   `Payments__Stripe__WebhookSecret=whsec_…` through the secret mechanism only
   (Azure App Service settings / Key Vault, GitHub Actions Secrets, or env vars — **never**
   `appsettings.json`; `.gitleaks.toml` even blocks committing `sk_live_*`). See
   [Configuration & secrets](04-configuration-and-2fa.md).
2. Register your production webhook URL (`https://…/webhooks/payments/stripe`) in the Stripe
   dashboard and use the signing secret it gives you.
3. For real tax, swap `ITaxRateProvider` for a live engine (above). To add PayPal/Venmo or
   another PSP, add an `IPaymentGateway` + `IPaymentWebhookParser` — checkout is untouched.

## Why a seam instead of hard-coding Stripe

The portfolio ships a fully working store with **zero external accounts** (Mock), while
proving the real integration shape (Stripe) is a drop-in. Swapping providers — or adding
PayPal/Venmo (Braintree), Adyen, or a BNPL method — is a new `IPaymentGateway` +
`IPaymentWebhookParser` and a config change, with no impact on `CheckoutHandler`, inventory
reservation, tax, or the order lifecycle.
