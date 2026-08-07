using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Abstractions;

public interface IOrderRepository
{
    /// <summary>Atomically inserts the order and reserves stock. Returns false (rolled back) if any line is short.</summary>
    Task<bool> TryPlaceAsync(Order order, CancellationToken ct);

    Task MarkPaidAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct);

    /// <summary>Marks the order failed and releases its inventory reservations.</summary>
    Task MarkPaymentFailedAsync(Order order, string reason, DateTimeOffset now, CancellationToken ct);

    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Order?> GetByNumberAndEmailAsync(string orderNumber, string email, CancellationToken ct);

    Task<IReadOnlyList<Order>> GetForUserAsync(Guid userId, CancellationToken ct);
}
