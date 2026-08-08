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

## Sales tax & the rate table

Sales tax is computed by `StateSalesTaxCalculator` on the **subtotal** (shipping is not
taxed in this model), using the **destination state** from the shipping address:

- The state code is normalized (`"ca"` → `"CA"`).
- Its base rate is looked up in the rate table; an **unknown or missing state → 0%**.
- `tax = round(subtotal × rate, 2, away-from-zero)`.
- The result is `(stateCode, rate, amount)`, and the order **snapshots** `tax_state`,
  `tax_rate`, and `tax` — so the exact rate charged is preserved on that order forever,
  even if the table later changes.

States with **no** state sales tax — **AK, DE, MT, NH, OR** — correctly resolve to **$0**.
(There is no "tax credit" or discount concept: tax is *added* per destination state, and
no-tax states simply yield zero.)

### Where the rates come from — and when they update

Rates come from an `ITaxRateProvider`. The default, `StaticStateTaxRateProvider`, is an
**offline, versioned** table of the 50 states + DC base rates compiled into the app. Its
freshness is made explicit by two fields on the rate set:

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

**Deliberate simplification:** real US sales tax is destination-based across thousands of
local/county/city jurisdictions, with product-category exemptions and economic-nexus rules.
This app uses a single **state-level base rate** as a documented approximation — enough to
show correct, server-side, snapshotted tax handling — with the seam in place so a real engine
replaces it without touching checkout.

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
