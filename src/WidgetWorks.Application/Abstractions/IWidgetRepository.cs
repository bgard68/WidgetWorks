using WidgetWorks.Domain.Catalog;

namespace WidgetWorks.Application.Abstractions;

/// <summary>Filter/paging criteria for catalog listing and search.</summary>
public sealed record WidgetQuery(string? Search, bool ActiveOnly, int Page, int PageSize)
{
    public int Offset => (Math.Max(1, Page) - 1) * PageSize;
}

public interface IWidgetRepository
{
    Task<Widget?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Widget?> GetBySkuAsync(string normalizedSku, CancellationToken ct);

    Task<IReadOnlyList<Widget>> SearchAsync(WidgetQuery query, CancellationToken ct);

    Task<int> CountAsync(WidgetQuery query, CancellationToken ct);

    Task AddAsync(Widget widget, CancellationToken ct);

    Task UpdateAsync(Widget widget, CancellationToken ct);

    /// <summary>How many order lines reference this widget — zero means it can be deleted outright.</summary>
    Task<int> CountOrderLinesAsync(Guid widgetId, CancellationToken ct);

    /// <summary>Permanently removes a widget. Only safe when it has no order history.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);
}
