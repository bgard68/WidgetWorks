namespace WidgetWorks.Domain.Carts;

/// <summary>A shopping cart. UserId is null for a guest cart; a registered user has at most one.</summary>
public sealed class Cart
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<CartItem> Items { get; set; } = [];
}

public sealed class CartItem
{
    public Guid Id { get; set; }

    public Guid CartId { get; set; }

    public Guid WidgetId { get; set; }

    public int Quantity { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
