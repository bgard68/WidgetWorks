using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Application.Carts;

namespace WidgetWorks.Application.Carts.RemoveItem;

public sealed record RemoveCartItemCommand(Guid CartId, Guid WidgetId, Guid? RequestedBy);

public sealed class RemoveCartItemHandler(ICartRepository carts, IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<CartView>> Handle(RemoveCartItemCommand command, CancellationToken ct)
    {
        var cart = await carts.GetAsync(command.CartId, ct);
        if (cart is null)
        {
            return Result<CartView>.Fail("Cart not found.");
        }

        if (!CartAccess.IsPermitted(cart, command.RequestedBy))
        {
            // Deliberately the same answer as a missing cart: telling an unauthorized caller that
            // the cart exists would confirm a guess.
            return Result<CartView>.Fail("Cart not found.");
        }

        await carts.RemoveItemAsync(command.CartId, command.WidgetId, ct);
        await carts.TouchAsync(command.CartId, clock.GetUtcNow(), ct);

        var updated = await carts.GetAsync(command.CartId, ct);
        return Result<CartView>.Success(await CartAssembler.BuildAsync(updated!, widgets, ct));
    }
}
