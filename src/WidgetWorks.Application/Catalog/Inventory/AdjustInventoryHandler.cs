using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Catalog.Inventory;

/// <summary>Applies a signed delta to on-hand stock (positive = restock, negative = shrinkage).</summary>
public sealed record AdjustInventoryCommand(Guid Id, int QuantityOnHandDelta);

public sealed class AdjustInventoryHandler(IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<int>> Handle(AdjustInventoryCommand command, CancellationToken ct)
    {
        var widget = await widgets.GetByIdAsync(command.Id, ct);
        if (widget is null)
        {
            return Result<int>.Fail("Widget not found.");
        }

        if (widget.IsArchived)
        {
            return Result<int>.Fail("Widget is archived and can no longer be restocked.");
        }

        var newOnHand = widget.QuantityOnHand + command.QuantityOnHandDelta;
        if (newOnHand < 0)
        {
            return Result<int>.Fail("On-hand quantity cannot go negative.");
        }

        if (newOnHand < widget.QuantityReserved)
        {
            return Result<int>.Fail("On-hand cannot drop below the reserved quantity.");
        }

        widget.QuantityOnHand = newOnHand;
        widget.UpdatedAt = clock.GetUtcNow();
        await widgets.UpdateAsync(widget, ct);

        return Result<int>.Success(widget.QuantityAvailable);
    }
}
