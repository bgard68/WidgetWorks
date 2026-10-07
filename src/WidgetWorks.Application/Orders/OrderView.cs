using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Orders;

public sealed record OrderItemView(Guid WidgetId, string Sku, string Name, decimal UnitPrice, int Quantity, decimal LineSubtotal);

public sealed record OrderView(
    Guid Id,
    string OrderNumber,
    string Status,
    string Email,
    decimal Subtotal,
    string ShippingMethod,
    decimal Shipping,
    string TaxState,
    decimal TaxRate,
    decimal Tax,
    decimal Total,
    string? PaymentProvider,
    string? PaymentReference,
    string? TrackingNumber,
    DateTimeOffset CreatedAt,
    /// <summary>Set while the charge outcome is unknown — staff need to see this, not just the logs.</summary>
    DateTimeOffset? PaymentUnconfirmedAt,
    /// <summary>Cumulative amount refunded. Staff need the figure, not just the status.</summary>
    decimal RefundedTotal,
    IReadOnlyList<OrderItemView> Items)
{
    public static OrderView From(Order o) => new(
        o.Id,
        o.OrderNumber,
        o.Status,
        o.Email,
        o.Subtotal,
        o.ShippingMethod,
        o.Shipping,
        o.TaxState,
        o.TaxRate,
        o.Tax,
        o.Total,
        o.PaymentProvider,
        o.PaymentReference,
        o.TrackingNumber,
        o.CreatedAt,
        o.PaymentUnconfirmedAt,
        o.RefundedTotal,
        o.Items.Select(i => new OrderItemView(i.WidgetId, i.Sku, i.Name, i.UnitPrice, i.Quantity, i.LineSubtotal)).ToList());
}

public sealed record OrderSummary(
    Guid Id,
    string OrderNumber,
    string Status,
    decimal Total,
    int ItemCount,
    DateTimeOffset CreatedAt,
    string? Email = null,
    DateTimeOffset? PaymentUnconfirmedAt = null)
{
    public static OrderSummary From(Order o) => new(o.Id, o.OrderNumber, o.Status, o.Total, o.UnitCount, o.CreatedAt, o.Email, o.PaymentUnconfirmedAt);
}
