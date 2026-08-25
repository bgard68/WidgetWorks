using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Catalog.Delete;

public sealed record DeleteWidgetCommand(Guid Id);

/// <summary>What actually happened to the widget, so the caller can say so plainly.</summary>
public enum DeleteWidgetOutcome
{
    /// <summary>No order history — the row was removed permanently.</summary>
    Deleted,

    /// <summary>Had order history — retired from sale but kept so those orders stay reportable.</summary>
    Archived,
}

public sealed record DeleteWidgetResult(DeleteWidgetOutcome Outcome, int OrderLineCount);

/// <summary>
/// Removes a widget from the catalog.
///
/// A widget that has never been ordered carries no history, so it is deleted
/// outright. One that appears on orders is archived instead: order_items still
/// references it, and those orders must stay reportable, so the row is retained
/// and marked archived (and deactivated, which closes the storefront, product
/// page and add-to-cart paths that already guard on IsActive).
/// </summary>
public sealed class DeleteWidgetHandler(IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<DeleteWidgetResult>> Handle(DeleteWidgetCommand command, CancellationToken ct)
    {
        var widget = await widgets.GetByIdAsync(command.Id, ct);
        if (widget is null)
        {
            return Result<DeleteWidgetResult>.Fail("Widget not found.");
        }

        if (widget.IsArchived)
        {
            return Result<DeleteWidgetResult>.Fail("Widget is already archived.");
        }

        var orderLines = await widgets.CountOrderLinesAsync(command.Id, ct);
        if (orderLines == 0)
        {
            await widgets.DeleteAsync(command.Id, ct);
            return Result<DeleteWidgetResult>.Success(new DeleteWidgetResult(DeleteWidgetOutcome.Deleted, 0));
        }

        var now = clock.GetUtcNow();
        await widgets.ArchiveAsync(command.Id, now, ct);

        return Result<DeleteWidgetResult>.Success(new DeleteWidgetResult(DeleteWidgetOutcome.Archived, orderLines));
    }
}
