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

    /// <summary>
    /// Human-quotable order number: WW-{date}-{10 chars}, e.g. WW-20260501-A1B2C3D4E5.
    ///
    /// The suffix is the head of the order's own v4 Guid, so it is random rather than sequential -
    /// an order number cannot be incremented to reach the next customer's. Its width is the part
    /// that matters: order_number carries a unique index, so a collision is not a data leak but it
    /// is a failed checkout, and collisions arrive by the birthday bound rather than when the space
    /// runs out. Six characters is 24 bits, which is a coin flip at roughly five thousand orders in
    /// a single day; ten characters is 40 bits, which stays under a rounding error past a million.
    /// The cost of the extra four characters is four characters.
    /// </summary>
    public static string NumberFor(DateTimeOffset now, Guid orderId)
        => $"WW-{now:yyyyMMdd}-{orderId.ToString("N")[..10].ToUpperInvariant()}";
}
