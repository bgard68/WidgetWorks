using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Carts.Merge;

/// <summary>Merges a guest cart into the signed-in user's cart, then discards the guest cart.</summary>
public sealed record MergeCartCommand(Guid UserId, Guid GuestCartId);

public sealed class MergeCartHandler(ICartRepository carts, IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<CartView>> Handle(MergeCartCommand command, CancellationToken ct)
    {
        var guest = await carts.GetAsync(command.GuestCartId, ct);
        if (guest is null)
        {
            return Result<CartView>.Fail("Guest cart not found.");
        }

        // Never merge another registered user's cart.
        if (guest.UserId is { } owner && owner != command.UserId)
        {
            return Result<CartView>.Fail("Cart not found.");
        }

        var now = clock.GetUtcNow();
        var target = await carts.GetByUserAsync(command.UserId, ct) ?? await carts.CreateAsync(command.UserId, ct);

        foreach (var item in guest.Items)
        {
            var widget = await widgets.GetByIdAsync(item.WidgetId, ct);
            if (widget is null || !widget.IsActive)
            {
                continue;
            }

            var existing = target.Items.FirstOrDefault(i => i.WidgetId == item.WidgetId);
            var desired = Math.Min((existing?.Quantity ?? 0) + item.Quantity, widget.QuantityAvailable);
            if (desired <= 0)
            {
                continue;
            }

            await carts.UpsertItemAsync(target.Id, item.WidgetId, desired, now, ct);
        }

        if (guest.Id != target.Id)
        {
            await carts.DeleteAsync(guest.Id, ct);
        }

        await carts.TouchAsync(target.Id, now, ct);
        var updated = await carts.GetAsync(target.Id, ct);
        return Result<CartView>.Success(await CartAssembler.BuildAsync(updated!, widgets, ct));
    }
}
