using WidgetWorks.Application.Carts;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Checkout.PlaceOrder;

/// <summary>
/// Turns a priced cart and a shipping address into the order that will be persisted. Separated from
/// <see cref="CheckoutHandler"/> so the shape of an order — its number format, which fields are
/// trimmed, what gets snapshotted — can change without touching the payment sequence.
/// </summary>
public static class OrderDraft
{
    public static Order Create(
        CartView cart,
        PricedCart priced,
        ShippingAddressInput ship,
        string email,
        Guid? userId,
        DateTimeOffset now,
        Guid orderId,
        Func<Guid> newItemId)
    {
        return new Order
        {
            Id = orderId,
            OrderNumber = NumberFor(now, orderId),
            UserId = userId,
            Email = email,
            ShipName = ship.Name?.Trim() ?? string.Empty,
            ShipLine1 = ship.Line1.Trim(),
            ShipLine2 = string.IsNullOrWhiteSpace(ship.Line2) ? null : ship.Line2.Trim(),
            ShipCity = ship.City.Trim(),
            ShipState = ship.State.Trim().ToUpperInvariant(),
            ShipPostalCode = ship.PostalCode.Trim(),
            ShipCountry = string.IsNullOrWhiteSpace(ship.Country) ? "US" : ship.Country.Trim().ToUpperInvariant(),
            Subtotal = priced.Subtotal,
            ShippingMethod = priced.ShippingMethod,
            Shipping = priced.Shipping,

            // Snapshot the tax that was actually charged: a later rate change must not rewrite history.
            TaxState = priced.StateCode,
            TaxRate = priced.TaxRate,
            Tax = priced.Tax,
            Total = priced.Total,
            Status = OrderStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Items = cart.Items.Select(l => new OrderItem
            {
                Id = newItemId(),
                WidgetId = l.WidgetId,
                Sku = l.Sku,
                Name = l.Name,
                UnitPrice = l.UnitPrice,
                Quantity = l.Quantity,
                LineSubtotal = l.LineSubtotal,
            }).ToList(),
        };
    }

    /// <summary>Human-quotable order number: WW-{date}-{6 chars}, e.g. WW-20260501-A1B2C3.</summary>
    public static string NumberFor(DateTimeOffset now, Guid orderId)
        => $"WW-{now:yyyyMMdd}-{orderId.ToString("N")[..6].ToUpperInvariant()}";
}
