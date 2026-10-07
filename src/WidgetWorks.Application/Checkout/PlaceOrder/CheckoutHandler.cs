using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Carts;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Checkout.PlaceOrder;

public sealed record ShippingAddressInput(string Name, string Line1, string? Line2, string City, string State, string PostalCode, string? Country);

public sealed record CheckoutCommand(
    Guid CartId,
    Guid? UserId,
    string Email,
    ShippingAddressInput ShipTo,
    string? ShippingMethod,
    string? PaymentToken);

public sealed record CheckoutResult(
    string OrderNumber,
    Guid OrderId,
    string Status,
    decimal Total,
    string PaymentProvider,
    string PaymentReference,
    string? ClientSecret = null,
    string? NextActionUrl = null);

/// <summary>
/// Places an order: re-prices the cart server-side (never trusting client totals), reserves stock and
/// persists a pending order atomically, then authorizes payment. A synchronous success finalizes
/// immediately (clears the cart, emails a receipt); a decline releases the reservation; an asynchronous
/// authorization parks the order in AwaitingPayment until a provider webhook settles it.
/// </summary>
public sealed class CheckoutHandler(
    ICartRepository carts,
    IWidgetRepository widgets,
    IOrderRepository orders,
    OrderPricer pricer,
    IPaymentGateway payments,
    IEmailSender email,
    IReconciliationSignal reconciliation,
    TimeProvider clock,
    ILogger<CheckoutHandler> logger)
{
    public async Task<Result<CheckoutResult>> Handle(CheckoutCommand command, CancellationToken ct)
    {
        static Result<CheckoutResult> Fail(string error) => Result<CheckoutResult>.Fail(error);

        var normalizedEmail = (command.Email ?? string.Empty).Trim();
        if (!normalizedEmail.Contains('@'))
        {
            return Fail("A valid email is required.");
        }

        var ship = command.ShipTo;
        if (ship is null ||
            string.IsNullOrWhiteSpace(ship.Line1) ||
            string.IsNullOrWhiteSpace(ship.City) ||
            string.IsNullOrWhiteSpace(ship.State) ||
            string.IsNullOrWhiteSpace(ship.PostalCode))
        {
            return Fail("A complete shipping address is required.");
        }

        var cart = await carts.GetAsync(command.CartId, ct);
        if (cart is null || !CartAccess.IsPermitted(cart, command.UserId))
        {
            // One answer for "no such cart" and "not yours": checking out someone else's basket
            // would otherwise disclose its contents in the resulting order.
            return Fail("Cart not found.");
        }

        var view = await CartAssembler.BuildAsync(cart, widgets, ct);
        if (view.ItemCount == 0)
        {
            return Fail("Your cart is empty.");
        }

        // Re-price server-side; never trust client-supplied totals.
        var priced = pricer.Price(view, ship.State, command.ShippingMethod);

        var now = clock.GetUtcNow();
        var order = OrderDraft.Create(view, priced, ship, normalizedEmail, command.UserId, now, Guid.NewGuid(), Guid.NewGuid);

        var placed = await orders.TryPlaceAsync(order, ct);
        if (!placed)
        {
            return Fail("One or more items are no longer available in the requested quantity.");
        }

        var payment = await payments.ChargeAsync(
            new PaymentRequest(order.OrderNumber, order.Total, "usd", order.Email, command.PaymentToken), ct);

        if (payment.Status == PaymentStatus.Indeterminate)
        {
            // The provider never said what happened, so the money may already be gone. Treating this
            // as a decline would release the reservation and tell the customer their payment failed —
            // the one mistake worth engineering around here. Park it instead: the reservation stays
            // put, the stale sweep leaves it alone, and reconciliation settles it from the provider's
            // own record.
            if (!await orders.MarkPaymentUnconfirmedAsync(order.Id, payment.Provider, clock.GetUtcNow(), ct))
            {
                return Fail("The payment could not be confirmed. Please check your orders before retrying.");
            }

            order.Status = OrderStatus.AwaitingPayment;

            // The cart goes, exactly as it does for an async authorization. Leaving it would let the
            // shopper re-submit a basket whose payment may already have been taken, and a second order
            // under a fresh key is precisely the duplicate this whole change exists to prevent. The
            // order is the record now; if reconciliation finds it failed, they re-add and try again.
            await carts.DeleteAsync(cart.Id, ct);

            // Tell the reconciler now rather than leaving it to find out on its next sweep. This order
            // is holding stock over a charge that may have been taken, so minutes matter.
            reconciliation.Notify();

            logger.LogError(
                "Order {OrderNumber} placed with an unconfirmed charge at {Provider}; awaiting reconciliation. {Error}",
                order.OrderNumber,
                payment.Provider,
                payment.Error);

            return Result<CheckoutResult>.Success(new CheckoutResult(
                order.OrderNumber, order.Id, OrderStatus.AwaitingPayment, order.Total,
                payment.Provider, string.Empty));
        }

        if (payment.Status == PaymentStatus.Declined)
        {
            // Discarded deliberately: this order was created moments ago and is still Pending, so
            // the compare-and-set cannot decline. A webhook arriving later is the contended path,
            // and ConfirmPaymentHandler is where the answer is acted on.
            _ = await orders.MarkPaymentFailedAsync(order, payment.Error ?? "Payment failed.", clock.GetUtcNow(), ct);
            return Fail(payment.Error ?? "Payment failed.");
        }

        var reference = payment.Reference ?? string.Empty;

        if (payment.Status == PaymentStatus.Pending)
        {
            // Async settlement (redirect/BNPL): keep the reservation, park the order, and let the
            // provider webhook finalize it. The receipt email is sent on confirmation, not here.
            _ = await orders.MarkAwaitingPaymentAsync(order.Id, payment.Provider, reference, clock.GetUtcNow(), ct);
            order.Status = OrderStatus.AwaitingPayment;
            await carts.DeleteAsync(cart.Id, ct);

            return Result<CheckoutResult>.Success(new CheckoutResult(
                order.OrderNumber, order.Id, OrderStatus.AwaitingPayment, order.Total,
                payment.Provider, reference, payment.ClientSecret, payment.NextActionUrl));
        }

        // Synchronous success.
        _ = await orders.MarkPaidAsync(order.Id, payment.Provider, reference, clock.GetUtcNow(), ct);
        order.Status = OrderStatus.Paid;
        await carts.DeleteAsync(cart.Id, ct);

        try
        {
            await email.SendAsync(EmailTemplates.OrderReceived(order), ct);
        }
        catch (Exception ex)
        {
            // The card was charged; a notification error must not turn that into a
            // failed checkout. Logged so a missing receipt can be chased.
            logger.LogWarning(
                ex,
                "Receipt email failed for paid order {OrderNumber}; the order stands.",
                order.OrderNumber);
        }

        return Result<CheckoutResult>.Success(new CheckoutResult(
            order.OrderNumber, order.Id, OrderStatus.Paid, order.Total, payment.Provider, reference));
    }
}
