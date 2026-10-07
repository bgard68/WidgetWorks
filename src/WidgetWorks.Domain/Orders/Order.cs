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
    /// <summary>
    /// A settled order whose money has been given back. Reached only through the refund use case,
    /// never through the generic status endpoint — it is deliberately absent from
    /// <see cref="Transitions"/> so that no one can mark an order refunded without a refund actually
    /// having been issued at the provider.
    /// </summary>
    public const string Refunded = "Refunded";

    public const string Shipped = "Shipped";
    public const string Delivered = "Delivered";
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// The fulfilment state machine. Only a settled (Paid) order can ship or be cancelled, and only
    /// a shipped one can be delivered -- so an order still awaiting payment can never be dispatched.
    /// Everything absent from this table is a forbidden transition, including any move out of a
    /// terminal state.
    /// </summary>
    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
    {
        [Paid] = [Shipped, Cancelled],
        [Shipped] = [Delivered],
    };

    /// <summary>The statuses an order in <paramref name="from"/> may legally move to.</summary>
    public static IReadOnlyList<string> AllowedNext(string? from)
        => from is not null && Transitions.TryGetValue(from, out var next) ? next : [];

    public static bool CanTransition(string? from, string? to)
        => to is not null && AllowedNext(from).Contains(to, StringComparer.Ordinal);
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

    /// <summary>
    /// When the provider failed to tell us what became of the charge. Non-null means the order is
    /// awaiting <em>reconciliation</em>, not awaiting the customer — it holds its reservation and is
    /// exempt from the stale-reservation sweep until the real outcome is known.
    /// </summary>
    public DateTimeOffset? PaymentUnconfirmedAt { get; set; }

    /// <summary>
    /// How much of this order has been refunded so far. Cumulative, so several partial refunds cannot
    /// add up to more than was charged, and the order only becomes
    /// <see cref="OrderStatus.Refunded"/> once it reaches <see cref="Total"/> — a part-refunded order
    /// is still a paid order with goods owed.
    /// </summary>
    public decimal RefundedTotal { get; set; }

    /// <summary>
    /// One of the demo's showcase orders. Refunding, cancelling or advancing it would take an exhibit
    /// away permanently — nothing re-seeds orders — so the staff actions refuse it. Set by the seeder,
    /// never through the API.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>What may still be refunded.</summary>
    public decimal RefundableRemaining => Total - RefundedTotal;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<OrderItem> Items { get; set; } = [];

    /// <summary>Number of units on the order (quantities summed, not lines counted).</summary>
    public int UnitCount => Items.Sum(i => i.Quantity);

    public bool CanTransitionTo(string? target) => OrderStatus.CanTransition(Status, target);

    /// <summary>
    /// Applies a fulfilment transition, keeping the rule with the data it guards. A blank tracking
    /// number leaves the existing one alone rather than erasing it. Callers that want to report a
    /// refusal rather than throw should ask <see cref="CanTransitionTo"/> first.
    /// </summary>
    public void TransitionTo(string target, string? trackingNumber, DateTimeOffset now)
    {
        if (!CanTransitionTo(target))
        {
            throw new InvalidOperationException($"Cannot change status from {Status} to '{target}'.");
        }

        Status = target;
        TrackingNumber = string.IsNullOrWhiteSpace(trackingNumber) ? TrackingNumber : trackingNumber.Trim();
        UpdatedAt = now;
    }
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
