using WidgetWorks.Domain.Catalog;

namespace WidgetWorks.Application.Catalog;

/// <summary>Read model returned to storefront and admin clients.</summary>
public sealed record WidgetView(
    Guid Id,
    string Sku,
    string Name,
    string Description,
    string? ImageUrl,
    decimal Price,
    bool IsActive,
    int QuantityOnHand,
    int QuantityReserved,
    int QuantityAvailable)
{
    public static WidgetView From(Widget w) => new(
        w.Id,
        w.Sku,
        w.Name,
        w.Description,
        w.ImageUrl,
        w.Price,
        w.IsActive,
        w.QuantityOnHand,
        w.QuantityReserved,
        w.QuantityAvailable);
}
