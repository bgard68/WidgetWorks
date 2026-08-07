namespace WidgetWorks.Domain.Catalog;

/// <summary>A sellable widget together with its single-warehouse inventory counts.</summary>
public sealed class Widget
{
    public Guid Id { get; set; }

    /// <summary>Stock-keeping unit, stored normalized (upper-case). Unique across the catalog.</summary>
    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }

    public decimal Price { get; set; }

    public bool IsActive { get; set; } = true;

    public int QuantityOnHand { get; set; }

    public int QuantityReserved { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Sellable stock = on hand minus reserved, never negative.</summary>
    public int QuantityAvailable => Math.Max(0, QuantityOnHand - QuantityReserved);

    public bool IsInStock => QuantityAvailable > 0;
}
