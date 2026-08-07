using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Catalog.Detail;

public sealed record GetWidgetQuery(Guid Id, bool IncludeInactive);

public sealed class GetWidgetHandler(IWidgetRepository widgets)
{
    public async Task<Result<WidgetView>> Handle(GetWidgetQuery query, CancellationToken ct)
    {
        var widget = await widgets.GetByIdAsync(query.Id, ct);
        if (widget is null || (!widget.IsActive && !query.IncludeInactive))
        {
            return Result<WidgetView>.Fail("Widget not found.");
        }

        return Result<WidgetView>.Success(WidgetView.From(widget));
    }
}
