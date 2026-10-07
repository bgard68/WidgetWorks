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

## Retry safety on checkout

Settlement being idempotent only covers the second half of the story. `POST /checkout` is
reached by a browser form, and that request gets duplicated for ordinary reasons: a
double-click, a proxy retry, a client retry policy, a shopper who reloaded because the
spinner looked stuck. Each arrival used to mint a fresh order id and a fresh order number,
so nothing in the schema could catch the second one — two orders, two charges.

Clients opt in with a header:

```
POST /checkout
Idempotency-Key: 7f1c9b2e-5f2a-4d3b-8a21-9c0e4b6d8f10
```

The key is **claimed before any work starts**, in a `idempotency_keys` table whose primary
key is `(scope, key)`. The claim is a single `insert ... on conflict do nothing`, so two
simultaneous arrivals both run it and the database picks exactly one winner — the atomic
check-and-create that a read-then-write would get wrong. Only the winner reaches the
payment gateway.

| Second arrival finds | Answer | `code` |
|---|---|---|
| The first attempt still running | **409** — retry shortly | `checkout_in_flight` |
| The first attempt finished | **200** with the original response and `Idempotent-Replay: true` | — |
| The key used for a *different* body | **409** — the key is already spoken for | `idempotency_key_reused` |
| A stored response that cannot be read back | **409** — look the order up | `idempotency_replay_failed` |
| No key sent at all | Handled as before: no protection, no behaviour change | — |

A 409 carries a `code` next to the message because the two kinds mean opposite things: one says
*wait*, the other says *this will never work*. A status alone cannot tell them apart, and matching
on the prose breaks the first time someone rewords it. Only `checkout_in_flight` is worth retrying;
the SPA waits out up to four attempts over ~1.5s under the same key, and surfaces anything else at
once.

Two details that matter more than they look:

- **The stored response is the point.** Blocking a duplicate is half a solution; a client
  whose connection dropped also needs to find out what happened. Checkout deletes the cart
  on success, so without the ledger that retry could only ever be answered "Cart not
  found" — the shopper would be unable to learn their own order number. Failures are stored
  too: replaying a decline is correct, re-running one is a second trip to the gateway for a
  card that already said no.
- **A request that dies mid-flight keeps its key.** If the work throws, nothing releases
  the claim, because nobody knows whether the card was charged — later retries get 409
  until retention expires the key. Trying again under a *new* key is the shopper's
  decision to make, not the server's.

### The second layer: idempotency at the provider

Our ledger protects the **order**. It cannot protect the **money**, because it only sees requests
arriving at us — it is blind to a retry of the one outbound call to Stripe. Those are different
layers and a professional integration needs both.

`StripePaymentGateway` therefore sends its own `Idempotency-Key` on the PaymentIntent create, keyed
on the order number (`ux_orders_number` makes it unique, and checkout charges exactly once per
order). Stripe honours a key for 24 hours, so a replayed create returns the **original** intent
instead of charging again.

That key is what makes the retry above it safe. The adapter retries a charge whose outcome it never
learned — a dropped socket, a client timeout, a 5xx, a 429 — twice, over about a second
(`Payments:Stripe:RetryDelaysMs`). A `Retry-After` from a rate limit is honoured over the configured
backoff. A **4xx is never retried**: Stripe understood the request and answered it, and a declined
card is a decision, not a blip.

Retrying also doubles as reconciliation in the common case. If the first attempt did create a charge
and we simply lost the reply, the second attempt under the same key returns that very intent — so
the ambiguity resolves itself rather than needing a separate lookup.

### When the provider never says what happened

A 4xx is Stripe's answer. A timeout, or a 5xx on every attempt, is Stripe failing to give one — and
those are not the same thing, because the second may have taken the money. The tempting reading is
that no answer means no, and it is wrong: declining releases the stock reservation and tells the
customer their payment failed, which, if the money did move, is the one outcome a shop cannot take
back.

So the charge has a third outcome, `PaymentStatus.Indeterminate`, and the system is built around one
rule: **an order only ever moves on a definite answer.**

```
charge ──► Succeeded ─────────────────────────► Paid
       ──► Declined ──────────────────────────► PaymentFailed   (reservation released)
       ──► Pending ───────────────────────────► AwaitingPayment (webhook settles it)
       ──► Indeterminate ─────────────────────► AwaitingPayment (reservation HELD, exempt from expiry)
                                                      │  reconciliation probes the provider
                                                      ├─ succeeded ──► Paid          (+ the receipt that was never sent)
                                                      ├─ not completed ► PaymentFailed (reservation released)
                                                      ├─ in progress ─► reference recorded, back on the webhook path
                                                      └─ still unknown ► left exactly as it was
```

What checkout does with an indeterminate charge: parks the order in `AwaitingPayment`, **keeps the
stock reservation**, sets `orders.payment_unconfirmed_at`, deletes the cart, and logs an error naming
the order. No receipt — there is nothing to confirm yet. The cart goes for the same reason it does on
an async authorization: leaving it would invite the shopper to re-submit a basket that may already
have been paid for, and a second order under a fresh key is the duplicate all of this exists to
prevent.

`payment_unconfirmed_at` does one job: it **exempts the order from the stale-reservation sweep**.
Without that exemption, parking would only delay the original bug — the sweep would quietly fail a
paid order ninety minutes later instead of immediately.

### Reconciliation

Holding a reservation is only safe because something comes back to resolve it.
`ReconcileUnconfirmedPaymentsHandler` runs on each sweep tick, **before** the stale release (so that
by the time the sweep looks, every order it can see has a known payment outcome), and asks the
provider what really happened.

The probe is a **search, never a replayed create**. Replaying the create under the same key returns
the original intent when one exists — but *creates* one when it does not, and the orders being probed
are precisely those that may never have been charged. `StripePaymentGateway.ProbeAsync` therefore
queries `/v1/payment_intents/search` on the `order_number` metadata written at charge time.

Four answers, and what each is allowed to do:

| Probe finds | Action |
|---|---|
| `succeeded` | Mark Paid, record the reference, **send the receipt checkout could not** |
| `canceled` / `requires_payment_method` | Mark PaymentFailed — and only now release the stock |
| `processing` / `requires_action` | Record the reference and clear the mark: the order is back on the webhook path, and back under the expiry sweep so it cannot hold stock for ever |
| nothing, an error, or a status we do not recognise | **Nothing.** Left for the next pass |

That last row is the discipline. Stripe's search index lags its own writes by up to a minute, so an
empty result is not evidence of absence — acting on it would fail orders that had been paid seconds
earlier. An unrecognised status is treated the same way: a provider adding a state we have never seen
must not get an order failed on a guess.

An order still unresolved after `Reconciliation:EscalateAfterHours` (4h) is **escalated in the logs
and otherwise left alone** — it keeps its status and its reservation. If neither we nor the provider
can say what happened, the honest response is to put a human in front of it, not to decide. The log
line names the order and the provider so it can be settled by hand from the dashboard.

Recording the reference is what makes the webhook path work again, incidentally: an unconfirmed order
has no reference for `ConfirmPaymentHandler` to correlate on, and the moment reconciliation learns one
the ordinary settlement route takes over.

### How quickly an unconfirmed charge gets chased

Reconciliation rode the hourly reservation sweep at first, which was correct and too slow: an order
could hold stock for an hour before anyone asked the provider what had happened. For the one case
where the customer may already have been charged, an hour is the wrong answer.

A short timer would have been worse than the problem. Polling every minute keeps a serverless
database awake around the clock to look at a table that is empty on every healthy day — the exact
cost the hourly interval exists to avoid.

So the worker wakes on demand. Checkout rings `IReconciliationSignal` the moment it parks an order,
and `PaymentReconciliationSweeper` then stays on a short cycle (`BusyIntervalSeconds`, 60s) only while
orders remain unresolved, falling back to a long idle wait (`IdleIntervalMinutes`, 30m) once the queue
is clear. On a good day it sleeps and touches nothing; during an incident it is a minute behind. The
signal is in-memory and best-effort, so the reservation sweep keeps its own reconciliation pass as the
backstop for anything parked before a restart.

### What staff can actually see

A mechanism nobody can observe is not finished. Both of these were log lines first, which is the same
as not existing — nobody should have to grep to find an order holding stock over an unknown payment.

`GET /admin/orders/payment-exceptions` (staff only; it exposes customer emails) returns two lists,
and the admin orders page shows them above the order list:

- **Unconfirmed** — charges the provider never resolved, each with how long it has been stuck. Empty
  on a healthy day, because reconciliation clears nearly all of them on its own. This is where you
  look when it is not.
- **Possible duplicates** — same customer, same total, inside `OrderReview:DuplicateWindowMinutes`
  (10m), neither side already failed or cancelled. Prevention is never perfect, so a shop needs
  somewhere a person can see what slipped through.

The duplicate list is a heuristic and is deliberately never acted on automatically. Two identical
orders minutes apart are usually a mistake and occasionally a customer who meant it, and no query can
tell those apart. Ten minutes is the span of a mistake; stretch it to hours and the queue starts
crying wolf, and a queue that cries wolf goes unread. A retry after a decline is excluded on both
sides — that is the system working, and nobody was charged twice.

### Refunds

`POST /admin/orders/{id}/refund` returns the full total and puts the stock back on sale. Three rules,
in this order, and the order is the design:

1. **Only a `Paid` order.** Once it has shipped, the money is half the question — the goods are in
   transit — and that is a returns workflow. The route refuses, and the UI hides the button, rather
   than pretending.
2. **The provider is asked first.** The order is marked `Refunded` only once Stripe agrees. Writing
   the status first would leave an order claiming a refund over money still in the account.
3. **An unconfirmed refund is not a failure.** A 5xx or a timeout leaves the outcome unknown, so the
   order is left exactly as it was and the message says to check the provider — telling staff it
   failed is how a customer gets paid back twice.

Partial refunds are supported: `POST` with `{ "amount": 5.00 }`, or omit it for the whole remaining
balance. `orders.refunded_total` is cumulative, and the order only becomes `Refunded` once it reaches
the total — a part-refunded order is still `Paid`, because goods are still owed, and **stock comes back
only when nothing is owed**.

Two idempotency layers again, for the same reason as the charge. The provider key is
`refund-{orderNumber}-{newRunningTotal}`, so retrying one refund presents the same key and pays out
once, while a genuinely different partial refund later is legitimately a different key. The database
write is a compare-and-set on `refunded_total`, so two staff refunding at the same moment cannot both
apply their amount — the loser is declined rather than overpaying. If the provider pays out and that
write is then declined, the handler says so loudly and records `order.refund_unrecorded`: the money has
gone, so it cannot be reported as a simple failure.

`Refunded` is deliberately absent from the order state machine's transition table: the generic status
endpoint cannot move money, so it must not be able to claim a refund happened. The refund route is the
only way in, and there is a test for that.

### The audit trail

Money moving at a staff member's request needs more than a log line — nobody queries stdout a month
later when asked who refunded an order. Every order action writes to `audit_events`, the same table
that already holds logins and lockouts:

| Action | Written when |
|---|---|
| `order.refunded` / `order.refunded_partial` | A refund succeeded, with the amount, provider reference and running total |
| `order.refund_refused` | The provider refused |
| `order.refund_unconfirmed` | The outcome is unknown — recorded precisely because the order did *not* change |
| `order.refund_unrecorded` | The provider paid out but the order had already moved; needs reconciling by hand |
| `order.status_changed` | A fulfilment transition, recording what it moved **from** as well as to |
| `order.reconciled_paid` / `order.reconciled_failed` | Reconciliation settled an unconfirmed charge (null actor — a system action) |

Entries are written *after* the change lands, so the trail never claims something that did not happen,
and the failed and unknown attempts are recorded as well as the successes — a trail holding only
successes would hide the attempt most worth finding. Status values come from domain constants rather
than the request body, which keeps a caller's newlines from forging entries.

### Watching it

Each reconciliation pass logs one warning line carrying the count of orders still unresolved, which is
what a log-based alert rule can watch; the per-order errors say *which*, this says *how bad*. The same
figure is in the review list for a dashboard. That is the honest limit of what is here: there is no
pager, so monitoring has to be wired up outside the app.

### What the client has to get right

Uniqueness of the key is the **client's** responsibility, and it cannot be anything else: a key is
the caller asserting "this is the same request I sent before," and the server has no way to
contradict it. Identical key plus identical body is indistinguishable from the retry this feature
exists to serve, so a client that reuses a key for a genuinely new purchase gets one order and two
successes.

A key may contain only letters, digits and `- _ . : +`, and anything else is refused. That is a
log-safety guard as much as a validation one: the key is written to the log, and it arrives in a
request header, so a value carrying newlines could forge whole entries and make an attacker's fiction
indistinguishable from the record (CWE-117). Rejecting beats escaping — nothing legitimate needs a
control character in an opaque token, so the narrow set costs callers nothing and they are told rather
than having their key quietly rewritten.

The rule is therefore **a fresh key per intent, not per connection** — `crypto.randomUUID()` when
the shopper commits to buying, reused for every retry of *that* attempt, discarded once the attempt
is resolved.

Two things keep a client honest rather than trusting it:

- A replayed response carries `Idempotent-Replay: true`, so a caller that believed it was placing a
  new order can tell that it wasn't, and every replay is logged with its key.
- Scoping the key to the **cart** closes the case structurally here: checkout deletes the cart on
  success, so a second genuine purchase has no cart to reference and cannot reuse the scope. A
  user-scoped or global key would have left it open.

What deliberately is *not* attempted: inferring intent. Any rule clever enough to catch a false
reuse — a time window, a payload heuristic — also catches the legitimate retry, which trades a real
bug for a hypothetical one.

Scope is the cart, not the customer: a key only has to be unique among attempts to buy one
basket, and a cart id is unguessable, so one shopper's key can never name another's order.
Keys are forgotten after **24 hours** (`Idempotency:RetentionHours`) by the same background
sweep that releases stale reservations — two jobs of the same kind, and one timer, which
keeps the serverless database asleep between passes.

The SPA sends a key per checkout attempt, held in a ref so a re-render or a second click
reuses it. It is retired only on a **400**: a declined card means whatever the shopper
changes next is genuinely a different request, and answering that with the original refusal
would trap them.

### The webhook endpoint

```
POST /webhooks/payments/{provider}
```

`{provider}` must match the configured `Payments:Provider` (`mock` or `stripe`): only that
provider's parser is registered, so the other name returns **404**. The raw body is read and
handed to the parser with the signature header (`Stripe-Signature`, or `X-Webhook-Signature`
for the mock). Responses: **404** unknown or inactive provider · **400** unverifiable/malformed ·
**200** acknowledged (with the resulting status, or `ignored` when no order matches).

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
| a token containing `decline` (e.g. `card-decline`), or exactly `4000000000000002` | **Declined** — reservation released, order **PaymentFailed** |
| a token containing `async`, `bnpl`, `klarna`, `afterpay`, `affirm`, `paylater`, `pay-later` or `pending` (e.g. `klarna_demo`) | **Pending** — order **AwaitingPayment**, settled by a webhook |
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

| Test PaymentMethod | Outcome |
|---|---|
| `pm_card_visa` (default when no token given) | Succeeds |
| `pm_card_chargeDeclined` | Card declined |
| `pm_card_chargeDeclinedInsufficientFunds` | Insufficient funds |

The token is sent as the PaymentIntent's `payment_method`, so it must be a `pm_…` id. Raw test card
numbers such as `4242 4242 4242 4242` only work through Stripe.js / Elements, which this app does
not integrate.

A card charge returns `succeeded` → order Paid. A PaymentIntent that doesn't settle immediately
(`requires_action` / `processing`) → **AwaitingPayment**, settled later by the Stripe webhook. The
gateway creates intents with `automatic_payment_methods[allow_redirects]=never`, so redirect-based
methods aren't offered until the SPA integrates Stripe's Payment Element.

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
