using WidgetWorks.Domain.Carts;

namespace WidgetWorks.Application.Abstractions;

public interface ICartRepository
{
    Task<Cart?> GetAsync(Guid cartId, CancellationToken ct);

    Task<Cart?> GetByUserAsync(Guid userId, CancellationToken ct);

    Task<Cart> CreateAsync(Guid? userId, CancellationToken ct);

    Task UpsertItemAsync(Guid cartId, Guid widgetId, int quantity, DateTimeOffset now, CancellationToken ct);

    Task RemoveItemAsync(Guid cartId, Guid widgetId, CancellationToken ct);

    Task DeleteAsync(Guid cartId, CancellationToken ct);

    Task TouchAsync(Guid cartId, DateTimeOffset now, CancellationToken ct);
}
