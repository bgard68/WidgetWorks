using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Carts;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class CartRepository(IDbConnectionFactory factory, TimeProvider clock) : ICartRepository
{
    public async Task<Cart?> GetAsync(Guid cartId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var cart = await db.QuerySingleOrDefaultAsync<Cart>(
            "select id, user_id, created_at, updated_at from carts where id = @cartId",
            new { cartId });
        if (cart is null)
        {
            return null;
        }

        await LoadItemsAsync(db, cart, ct);
        return cart;
    }

    public async Task<Cart?> GetByUserAsync(Guid userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var cart = await db.QuerySingleOrDefaultAsync<Cart>(
            "select id, user_id, created_at, updated_at from carts where user_id = @userId",
            new { userId });
        if (cart is null)
        {
            return null;
        }

        await LoadItemsAsync(db, cart, ct);
        return cart;
    }

    public async Task<Cart> CreateAsync(Guid? userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var now = clock.GetUtcNow();
        var cart = new Cart { Id = Guid.NewGuid(), UserId = userId, CreatedAt = now, UpdatedAt = now };
        await db.ExecuteAsync(
            "insert into carts (id, user_id, created_at, updated_at) values (@Id, @UserId, @CreatedAt, @UpdatedAt)",
            cart);
        return cart;
    }

    public async Task UpsertItemAsync(Guid cartId, Guid widgetId, int quantity, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into cart_items (id, cart_id, widget_id, quantity, added_at)
              values (@Id, @cartId, @widgetId, @quantity, @now)
              on conflict (cart_id, widget_id) do update set quantity = excluded.quantity",
            new { Id = Guid.NewGuid(), cartId, widgetId, quantity, now });
    }

    public async Task RemoveItemAsync(Guid cartId, Guid widgetId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "delete from cart_items where cart_id = @cartId and widget_id = @widgetId",
            new { cartId, widgetId });
    }

    public async Task DeleteAsync(Guid cartId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync("delete from carts where id = @cartId", new { cartId });
    }

    public async Task TouchAsync(Guid cartId, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync("update carts set updated_at = @now where id = @cartId", new { cartId, now });
    }

    private static async Task LoadItemsAsync(System.Data.IDbConnection db, Cart cart, CancellationToken ct)
    {
        var items = await db.QueryAsync<CartItem>(
            "select id, cart_id, widget_id, quantity, added_at from cart_items where cart_id = @cartId order by added_at",
            new { cartId = cart.Id });
        cart.Items = items.ToList();
    }
}
