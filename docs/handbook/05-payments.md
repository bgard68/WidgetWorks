# 5. Payments & testing credit cards

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

## How checkout uses it

`CheckoutHandler` (see [Architecture](02-architecture.md)) does the safe thing:

1. **Re-prices server-side** — recomputes subtotal, shipping, and per-state tax from the
   cart; the client’s numbers are ignored.
2. **Reserves stock + persists a pending order atomically** (a Dapper transaction).
3. **Charges** via `IPaymentGateway.ChargeAsync` for the server-computed total.
4. **Finalizes on the outcome** — `ChargeAsync` returns one of three results:
   - **Succeeded** (synchronous, e.g. a card): mark Paid, clear the cart, email a receipt
     (best-effort).
   - **Declined**: release the reservation and mark PaymentFailed.
   - **Pending** (asynchronous, e.g. BNPL/redirect): keep the reservation, park the order in
     **AwaitingPayment**, and wait for a provider **webhook** to settle it (see below).

No card numbers ever touch the app or the database — only a payment **token** and, after a
charge, the gateway’s reference id. That keeps the app out of PCI scope.

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
  payment settles, so it can’t be oversold; a failure releases it.
- **The order state machine still guards fulfillment.** Only a **Paid** order can be
  Shipped, so an admin can’t ship something that hasn’t settled — an `AwaitingPayment`
  order returns `400` on a status change.
- **Webhooks are verified, then normalized.** Each provider implements
  `IPaymentWebhookParser`, which verifies the payload’s signature and maps it to a
  normalized `PaymentEvent(Provider, Reference, Type)`. `ConfirmPaymentHandler` looks the
  order up by `(provider, reference)` and transitions it.
- **Settlement is idempotent.** Providers retry webhooks, so a duplicate delivery — or an
  event for an order that already moved on — is a no-op that returns the current status.
  Only an `AwaitingPayment` order transitions.

### The webhook endpoint

```
POST /webhooks/payments/{provider}
```

`{provider}` selects the parser (`mock`, `stripe`). The raw body is read and handed to the
parser with the signature header (`Stripe-Signature`, or `X-Webhook-Signature` for the
mock). Responses: **404** unknown provider · **400** unverifiable/malformed · **200**
acknowledged (with the resulting status, or `ignored` when no order matches — the standard
“ack so the provider stops retrying” behavior).

For **Stripe**, the parser verifies the `Stripe-Signature` header (HMAC-SHA256 of
`"{timestamp}.{payload}"` keyed by the `whsec_…` secret) and handles
`payment_intent.succeeded` / `payment_intent.payment_failed` / `payment_intent.canceled`.
Configure it with:

```
Payments__Stripe__WebhookSecret=whsec_...       # user-secrets / env, never committed
```

## Mock gateway (default) — testing without a provider

`MockPaymentGateway` approves any positive charge and returns a synthetic reference,
**except** when the payment token signals a decline or an asynchronous method — so you can
exercise all three paths deterministically:

| Payment token | Result |
|---|---|
| anything (e.g. `tok_visa_ok`) | Approved — order becomes **Paid** |
| a token containing `decline` (e.g. `card-decline`) | **Declined** — reservation released, order **PaymentFailed** |
| an async marker (`klarna…`, `bnpl…`, `async…`, `affirm…`, `afterpay…`) | **Pending** — order **AwaitingPayment**, settled by a webhook |
| amount ≤ 0 | Declined |

To settle a mock async order locally (no provider account, no signature needed by default):

```bash
curl -X POST http://localhost:8080/webhooks/payments/mock \
  -H 'Content-Type: application/json' \
  -d '{"reference":"<paymentReference from checkout>","outcome":"succeeded"}'
```

Use `"outcome":"failed"` to drive the failure path. The smoke test exercises both, plus the
404/400 guardrails and idempotency. (An optional `Payments:Mock:WebhookSecret` turns on the
shared-secret `X-Webhook-Signature` check when you want to demo verification.)

## Stripe test mode (optional)

`StripePaymentGateway` creates and confirms a **PaymentIntent** against Stripe’s REST API
over HTTPS (no SDK dependency). Enable it with:

```
Payments__Provider=Stripe
Payments__Stripe__SecretKey=sk_test_...        # TEST key only — user-secrets / env, never committed
```

Then use Stripe’s standard **test** instruments (test mode only):

| Test PaymentMethod / card | Outcome |
|---|---|
| `pm_card_visa` (default when no token given) | Succeeds |
| `4242 4242 4242 4242` | Succeeds |
| `4000 0000 0000 0002` | Card declined |
| `4000 0000 0000 9995` | Insufficient funds |

A card charge returns `succeeded` → order Paid. A redirect/BNPL method returns
`requires_action`/`processing` → order **AwaitingPayment**, settled later by the Stripe
webhook above. **Live keys are never used** — `.gitleaks.toml` even blocks `sk_live_*`.

## Why a seam instead of hard-coding Stripe

The portfolio ships a fully working store with **zero external accounts** (Mock), while
proving the real integration shape (Stripe) is a drop-in. Swapping providers — or adding
PayPal/Venmo (Braintree), Adyen, or a BNPL method — is a new `IPaymentGateway` +
`IPaymentWebhookParser` and a config change, with no impact on `CheckoutHandler`, inventory
reservation, or the order lifecycle. The asynchronous **AwaitingPayment → webhook** flow is
the same one a PayPal/Venmo or BNPL adapter uses, so those become a per-method addition
rather than a re-architecture.
