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

        if (payment.Status == PaymentStatus.Declined)
        {
            await orders.MarkPaymentFailedAsync(order, payment.Error ?? "Payment failed.", clock.GetUtcNow(), ct);
            return Fail(payment.Error ?? "Payment failed.");
        }

        var reference = payment.Reference ?? string.Empty;

        if (payment.Status == PaymentStatus.Pending)
        {
            // Async settlement (redirect/BNPL): keep the reservation, park the order, and let the
            // provider webhook finalize it. The receipt email is sent on confirmation, not here.
            await orders.MarkAwaitingPaymentAsync(order.Id, payment.Provider, reference, clock.GetUtcNow(), ct);
            order.Status = OrderStatus.AwaitingPayment;
            await carts.DeleteAsync(cart.Id, ct);

            return Result<CheckoutResult>.Success(new CheckoutResult(
                order.OrderNumber, order.Id, OrderStatus.AwaitingPayment, order.Total,
                payment.Provider, reference, payment.ClientSecret, payment.NextActionUrl));
        }

        // Synchronous success.
        await orders.MarkPaidAsync(order.Id, payment.Provider, reference, clock.GetUtcNow(), ct);
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
