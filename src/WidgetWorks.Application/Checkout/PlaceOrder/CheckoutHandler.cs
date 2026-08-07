using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Carts;
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

public sealed record CheckoutResult(string OrderNumber, Guid OrderId, string Status, decimal Total, string PaymentProvider, string PaymentReference);

/// <summary>
/// Places an order: re-prices the cart server-side (never trusting client totals), reserves stock and
/// persists a pending order atomically, charges the payment gateway, then finalizes -- releasing the
/// reservation if payment fails and clearing the cart if it succeeds.
/// </summary>
public sealed class CheckoutHandler(
    ICartRepository carts,
    IWidgetRepository widgets,
    IOrderRepository orders,
    IShippingCalculator shipping,
    ITaxCalculator tax,
    IPaymentGateway payments,
    TimeProvider clock)
{
    public async Task<Result<CheckoutResult>> Handle(CheckoutCommand command, CancellationToken ct)
    {
        static Result<CheckoutResult> Fail(string error) => Result<CheckoutResult>.Fail(error);

        var email = (command.Email ?? string.Empty).Trim();
        if (!email.Contains('@'))
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
        if (cart is null)
        {
            return Fail("Cart not found.");
        }

        var view = await CartAssembler.BuildAsync(cart, widgets, ct);
        if (view.ItemCount == 0)
        {
            return Fail("Your cart is empty.");
        }

        // Re-price server-side; never trust client-supplied totals.
        var shippingQuote = shipping.Calculate(command.ShippingMethod, view.Subtotal, view.ItemCount);
        var taxLine = tax.Calculate(ship.State, view.Subtotal);
        var total = view.Subtotal + shippingQuote.Amount + taxLine.Amount;

        var now = clock.GetUtcNow();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = $"WW-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            UserId = command.UserId,
            Email = email,
            ShipName = ship.Name?.Trim() ?? string.Empty,
            ShipLine1 = ship.Line1.Trim(),
            ShipLine2 = string.IsNullOrWhiteSpace(ship.Line2) ? null : ship.Line2.Trim(),
            ShipCity = ship.City.Trim(),
            ShipState = ship.State.Trim().ToUpperInvariant(),
            ShipPostalCode = ship.PostalCode.Trim(),
            ShipCountry = string.IsNullOrWhiteSpace(ship.Country) ? "US" : ship.Country.Trim().ToUpperInvariant(),
            Subtotal = view.Subtotal,
            ShippingMethod = shippingQuote.Method,
            Shipping = shippingQuote.Amount,
            TaxState = taxLine.StateCode,
            TaxRate = taxLine.Rate,
            Tax = taxLine.Amount,
            Total = total,
            Status = OrderStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Items = view.Items.Select(l => new OrderItem
            {
                Id = Guid.NewGuid(),
                WidgetId = l.WidgetId,
                Sku = l.Sku,
                Name = l.Name,
                UnitPrice = l.UnitPrice,
                Quantity = l.Quantity,
                LineSubtotal = l.LineSubtotal,
            }).ToList(),
        };

        var placed = await orders.TryPlaceAsync(order, ct);
        if (!placed)
        {
            return Fail("One or more items are no longer available in the requested quantity.");
        }

        var payment = await payments.ChargeAsync(
            new PaymentRequest(order.OrderNumber, order.Total, "usd", order.Email, command.PaymentToken), ct);

        if (!payment.Success)
        {
            await orders.MarkPaymentFailedAsync(order, payment.Error ?? "Payment failed.", clock.GetUtcNow(), ct);
            return Fail(payment.Error ?? "Payment failed.");
        }

        await orders.MarkPaidAsync(order.Id, payment.Provider, payment.Reference ?? string.Empty, clock.GetUtcNow(), ct);
        await carts.DeleteAsync(cart.Id, ct);

        return Result<CheckoutResult>.Success(new CheckoutResult(
            order.OrderNumber, order.Id, OrderStatus.Paid, order.Total, payment.Provider, payment.Reference ?? string.Empty));
    }
}
