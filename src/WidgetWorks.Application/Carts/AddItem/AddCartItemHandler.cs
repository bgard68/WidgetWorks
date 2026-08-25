using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Carts;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Carts.AddItem;

/// <summary>Adds a quantity of a widget to a cart, creating the cart when needed.</summary>
public sealed record AddCartItemCommand(Guid? CartId, Guid? UserId, Guid WidgetId, int Quantity);

public sealed class AddCartItemHandler(ICartRepository carts, IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<CartView>> Handle(AddCartItemCommand command, CancellationToken ct)
    {
        if (command.Quantity <= 0)
        {
            return Result<CartView>.Fail("Quantity must be at least 1.");
        }

        var widget = await widgets.GetByIdAsync(command.WidgetId, ct);
        if (widget is null || !widget.IsActive)
        {
            return Result<CartView>.Fail("Widget not found.");
        }

        var cart = await ResolveCartAsync(command.CartId, command.UserId, ct);
        var existing = cart.Items.FirstOrDefault(i => i.WidgetId == command.WidgetId);
        var desired = Math.Min((existing?.Quantity ?? 0) + command.Quantity, widget.QuantityAvailable);
        if (desired <= 0)
        {
            return Result<CartView>.Fail("This widget is out of stock.");
        }

        var now = clock.GetUtcNow();
        await carts.UpsertItemAsync(cart.Id, command.WidgetId, desired, now, ct);
        await carts.TouchAsync(cart.Id, now, ct);

        var updated = await carts.GetAsync(cart.Id, ct);
        return Result<CartView>.Success(await CartAssembler.BuildAsync(updated!, widgets, ct));
    }

    private async Task<Cart> ResolveCartAsync(Guid? cartId, Guid? userId, CancellationToken ct)
    {
        // A supplied id is only honoured when the caller may actually use that cart. A foreign one
        // falls through to the caller's own rather than erroring, so an attacker learns nothing about
        // whether the id existed and an honest client with a stale id simply carries on.
        if (cartId is { } id && await carts.GetAsync(id, ct) is { } existing && CartAccess.IsPermitted(existing, userId))
        {
            return existing;
        }

        if (userId is { } uid && await carts.GetByUserAsync(uid, ct) is { } userCart)
        {
            return userCart;
        }

        return await carts.CreateAsync(userId, ct);
    }
}
