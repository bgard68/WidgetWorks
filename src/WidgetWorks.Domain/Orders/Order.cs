namespace WidgetWorks.Domain.Orders;

public static class OrderStatus
{
    public const string Pending = "Pending";

    /// <summary>
    /// Order placed and stock reserved, but payment is settling asynchronously
    /// (a redirect/BNPL method). A provider webhook moves it to Paid or PaymentFailed.
    /// </summary>
    public const string AwaitingPayment = "AwaitingPayment";

    public const string Paid = "Paid";
    public const string PaymentFailed = "PaymentFailed";
    public const string Shipped = "Shipped";
    public const string Delivered = "Delivered";
    public const string Cancelled = "Cancelled";
}

/// <summary>A placed order with a shipping address, computed totals, payment result, and line items.</summary>
public sealed class Order
{
    public Guid Id { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    /// <summary>Null for a guest order.</summary>
    public Guid? UserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string ShipName { get; set; } = string.Empty;

    public string ShipLine1 { get; set; } = string.Empty;

    public string? ShipLine2 { get; set; }

    public string ShipCity { get; set; } = string.Empty;

    public string ShipState { get; set; } = string.Empty;

    public string ShipPostalCode { get; set; } = string.Empty;

    public string ShipCountry { get; set; } = "US";

    public decimal Subtotal { get; set; }

    public string ShippingMethod { get; set; } = string.Empty;

    public decimal Shipping { get; set; }

    public string TaxState { get; set; } = string.Empty;

    public decimal TaxRate { get; set; }

    public decimal Tax { get; set; }

    public decimal Total { get; set; }

    public string Status { get; set; } = OrderStatus.Pending;

    public string? PaymentProvider { get; set; }

    public string? PaymentReference { get; set; }

    public string? TrackingNumber { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<OrderItem> Items { get; set; } = [];
}

public sealed class OrderItem
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid WidgetId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public decimal LineSubtotal { get; set; }
}
