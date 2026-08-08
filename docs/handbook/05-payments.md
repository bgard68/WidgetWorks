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
4. **Finalizes** — on success: mark Paid, clear the cart, email a receipt (best-effort);
   on decline: release the reservation and mark PaymentFailed.

No card numbers ever touch the app or the database — only a payment **token** and, after a
charge, the gateway’s reference id. That keeps the app out of PCI scope.

## Mock gateway (default) — testing without a provider

`MockPaymentGateway` approves any positive charge and returns a synthetic reference,
**except** when the payment token signals a decline — so you can exercise both paths
deterministically:

| Payment token | Result |
|---|---|
| anything (e.g. `tok_visa_ok`) | Approved — order becomes **Paid** |
| a token containing `decline` (e.g. `card-decline`) | **Declined** — reservation released, order **PaymentFailed** |
| amount ≤ 0 | Declined |

This is what the storefront’s “Payment token (demo)” field drives, and what the smoke test
uses for its success and decline cases.

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

The adapter returns `succeeded` → order Paid; anything else → declined and the reservation
is released. **Live keys are never used** — `.gitleaks.toml` even blocks `sk_live_*`.

## Why a seam instead of hard-coding Stripe

The portfolio ships a fully working store with **zero external accounts** (Mock), while
proving the real integration shape (Stripe) is a drop-in. Swapping providers — or adding
PayPal, Adyen, Braintree — is a new `IPaymentGateway` and a config change, with no impact
on `CheckoutHandler`, inventory reservation, or the order lifecycle.
