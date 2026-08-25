using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Application.Carts;

namespace WidgetWorks.Application.Carts.UpdateItem;

/// <summary>Sets an absolute quantity for a line; a quantity of zero removes it.</summary>
public sealed record UpdateCartItemCommand(Guid CartId, Guid WidgetId, int Quantity, Guid? RequestedBy);

public sealed class UpdateCartItemHandler(ICartRepository carts, IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<CartView>> Handle(UpdateCartItemCommand command, CancellationToken ct)
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

        var now = clock.GetUtcNow();
        if (command.Quantity <= 0)
        {
            await carts.RemoveItemAsync(command.CartId, command.WidgetId, ct);
        }
        else
        {
            var widget = await widgets.GetByIdAsync(command.WidgetId, ct);
            if (widget is null || !widget.IsActive)
            {
                return Result<CartView>.Fail("Widget not found.");
            }

            var qty = Math.Min(command.Quantity, widget.QuantityAvailable);
            if (qty <= 0)
            {
                return Result<CartView>.Fail("This widget is out of stock.");
            }

            await carts.UpsertItemAsync(command.CartId, command.WidgetId, qty, now, ct);
        }

        await carts.TouchAsync(command.CartId, now, ct);
        var updated = await carts.GetAsync(command.CartId, ct);
        return Result<CartView>.Success(await CartAssembler.BuildAsync(updated!, widgets, ct));
    }
}
