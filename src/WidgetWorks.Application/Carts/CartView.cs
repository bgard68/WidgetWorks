namespace WidgetWorks.Application.Carts;

public sealed record CartLineView(
    Guid WidgetId,
    string Sku,
    string Name,
    decimal UnitPrice,
    int Quantity,
    int QuantityAvailable,
    decimal LineSubtotal);

public sealed record CartView(
    Guid Id,
    Guid? UserId,
    IReadOnlyList<CartLineView> Items,
    decimal Subtotal,
    int ItemCount);
