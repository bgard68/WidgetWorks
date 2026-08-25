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

        // Two layers, on purpose. These checks run against the row just read and exist to name the
        // reason accurately - a restock that would go negative is a different mistake from one that
        // would strand a reservation, and the operator should be told which. They are advisory: the
        // same rules are enforced inside the UPDATE, which is what makes them race-proof. Computing
        // them only here is what let a concurrent reservation be overwritten.
        var projected = widget.QuantityOnHand + command.QuantityOnHandDelta;
        if (projected < 0)
        {
            return Result<int>.Fail("On-hand quantity cannot go negative.");
        }

        if (projected < widget.QuantityReserved)
        {
            return Result<int>.Fail("On-hand cannot drop below the reserved quantity.");
        }

        var available = await widgets.AdjustStockAsync(command.Id, command.QuantityOnHandDelta, clock.GetUtcNow(), ct);
        if (available is null)
        {
            // Reachable only when the row changed between the read and the write - which, once the
            // checks above have passed, means stock was reserved in that gap. The statement refused
            // rather than stranding it.
            return Result<int>.Fail("On-hand cannot drop below the reserved quantity.");
        }

        return Result<int>.Success(available.Value);
    }
}
