using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Abstractions;

public interface IOrderRepository
{
    /// <summary>Atomically inserts the order and reserves stock. Returns false (rolled back) if any line is short.</summary>
    Task<bool> TryPlaceAsync(Order order, CancellationToken ct);

    /// <summary>Records the provider + reference and parks the order in AwaitingPayment (async settlement).</summary>
    Task MarkAwaitingPaymentAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct);

    Task MarkPaidAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct);

    /// <summary>Marks the order failed and releases its inventory reservations.</summary>
    Task MarkPaymentFailedAsync(Order order, string reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Persists a fulfilment transition together with the inventory movement it implies, in one
    /// transaction: shipping converts the reservation into a real stock decrement, cancelling
    /// releases it back. Pass the order after <see cref="Order.TransitionTo"/> has run - the new
    /// status on it decides the movement.
    /// </summary>
    Task UpdateStatusAsync(Order order, DateTimeOffset now, CancellationToken ct);

    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Finds an order by the payment provider + reference stored at authorization time (webhook correlation).</summary>
    Task<Order?> GetByPaymentReferenceAsync(string provider, string reference, CancellationToken ct);

    Task<Order?> GetByNumberAndEmailAsync(string orderNumber, string email, CancellationToken ct);

    Task<IReadOnlyList<Order>> GetForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Most recent orders across all customers — the staff view. Capped, not paged:
    /// staff want "what came in lately", and an unbounded scan is the wrong default.</summary>
    Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct);
}
