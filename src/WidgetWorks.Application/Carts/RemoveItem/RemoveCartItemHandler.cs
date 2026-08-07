using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Carts.RemoveItem;

public sealed record RemoveCartItemCommand(Guid CartId, Guid WidgetId);

public sealed class RemoveCartItemHandler(ICartRepository carts, IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<CartView>> Handle(RemoveCartItemCommand command, CancellationToken ct)
    {
        var cart = await carts.GetAsync(command.CartId, ct);
        if (cart is null)
        {
            return Result<CartView>.Fail("Cart not found.");
        }

        await carts.RemoveItemAsync(command.CartId, command.WidgetId, ct);
        await carts.TouchAsync(command.CartId, clock.GetUtcNow(), ct);

        var updated = await carts.GetAsync(command.CartId, ct);
        return Result<CartView>.Success(await CartAssembler.BuildAsync(updated!, widgets, ct));
    }
}
