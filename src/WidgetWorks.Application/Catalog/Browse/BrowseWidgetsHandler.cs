using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Catalog.Browse;

public sealed record BrowseWidgetsQuery(string? Search, bool IncludeInactive, int Page, int PageSize);

public sealed class BrowseWidgetsHandler(IWidgetRepository widgets)
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public async Task<PagedResult<WidgetView>> Handle(BrowseWidgetsQuery query, CancellationToken ct)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var size = query.PageSize is < 1 or > MaxPageSize ? DefaultPageSize : query.PageSize;

        var repoQuery = new WidgetQuery(
            string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            ActiveOnly: !query.IncludeInactive,
            page,
            size);

        var items = await widgets.SearchAsync(repoQuery, ct);
        var total = await widgets.CountAsync(repoQuery, ct);

        var views = items.Select(WidgetView.From).ToList();
        return new PagedResult<WidgetView>(views, page, size, total);
    }
}
