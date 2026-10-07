using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Abstractions;

public interface IOrderRepository
{
    /// <summary>Atomically inserts the order and reserves stock. Returns false (rolled back) if any line is short.</summary>
    Task<bool> TryPlaceAsync(Order order, CancellationToken ct);

    /// <summary>
    /// Records the provider + reference and parks the order in AwaitingPayment (async settlement).
    /// Returns false when the order had already moved on, so the write was declined.
    /// </summary>
    Task<bool> MarkAwaitingPaymentAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Parks the order in AwaitingPayment and records that the provider never said what became of the
    /// charge. Keeps the stock reservation and exempts the order from the stale-reservation sweep,
    /// because failing a charge that may have succeeded is the outcome this exists to avoid.
    /// Returns false when the order had already moved on.
    /// </summary>
    Task<bool> MarkPaymentUnconfirmedAsync(Guid orderId, string provider, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Records a provider reference discovered after the fact and clears the unconfirmed mark, leaving
    /// the order in AwaitingPayment.
    ///
    /// Separate from <see cref="MarkAwaitingPaymentAsync"/> because that one guards on Pending — it is
    /// the checkout path, and an order may only be parked once. This is the reconciliation path, where
    /// the order is already parked and the new fact is the reference: knowing it puts the order back
    /// on the ordinary webhook route, and clearing the mark returns it to the expiry sweep so it
    /// cannot hold stock for ever. Returns false if the order has since been settled.
    /// </summary>
    Task<bool> RecordPaymentReferenceAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Orders whose charge outcome is still unknown, oldest first, items loaded so a reconciled
    /// failure can release their reservations.
    /// </summary>
    Task<IReadOnlyList<Order>> GetUnconfirmedPaymentsAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Settles the order. Returns false when it was no longer awaiting settlement — a duplicate or
    /// out-of-order provider event, which must not overwrite a decided order.
    /// </summary>
    Task<bool> MarkPaidAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Marks the order failed and releases its inventory reservations, atomically. Returns false
    /// when the order had already moved on; the caller must treat that as "someone else handled it"
    /// rather than retrying, because the stock has already been dealt with.
    /// </summary>
    Task<bool> MarkPaymentFailedAsync(Order order, string reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Persists a fulfilment transition together with the inventory movement it implies, in one
    /// transaction: shipping converts the reservation into a real stock decrement, cancelling
    /// releases it back. Pass the order after <see cref="Order.TransitionTo"/> has run - the new
    /// status on it decides the movement.
    /// </summary>
    Task UpdateStatusAsync(Order order, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Orders still parked in AwaitingPayment since before <paramref name="cutoff"/>, items loaded
    /// so their reservations can be released. A settlement webhook that never arrives would
    /// otherwise hold that stock forever.
    /// </summary>
    /// <param name="limit">Caps one sweep, so a large backlog is worked through over several passes
    /// rather than in one long transaction.</param>
    Task<IReadOnlyList<Order>> GetStaleAwaitingPaymentAsync(DateTimeOffset cutoff, int limit, CancellationToken ct);

    /// <summary>
    /// Records money given back. <paramref name="refundedTotal"/> is the new cumulative figure, and the
    /// write is guarded on both the order still being Paid and the stored total still being lower — so
    /// two staff refunding at the same moment cannot both apply their amount. Stock is released only
    /// when <paramref name="fullyRefunded"/>; a part refund leaves goods owed and the units committed.
    /// Returns false when the guard declined, which the caller must treat as "someone else got there".
    /// </summary>
    Task<bool> RecordRefundAsync(Guid orderId, decimal refundedTotal, bool fullyRefunded, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Groups of orders that look like the same purchase made twice — same customer, same total, close
    /// together in time, neither one already failed or cancelled. Prevention is never perfect, so a
    /// shop needs somewhere a human can see what slipped through; this is the query behind that list.
    /// </summary>
    Task<IReadOnlyList<Order>> GetPossibleDuplicatesAsync(TimeSpan window, int limit, CancellationToken ct);

    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Finds an order by the payment provider + reference stored at authorization time (webhook correlation).</summary>
    Task<Order?> GetByPaymentReferenceAsync(string provider, string reference, CancellationToken ct);

    Task<Order?> GetByNumberAndEmailAsync(string orderNumber, string email, CancellationToken ct);

    Task<IReadOnlyList<Order>> GetForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Most recent orders across all customers — the staff view. Capped, not paged:
    /// staff want "what came in lately", and an unbounded scan is the wrong default.</summary>
    Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct);
}
