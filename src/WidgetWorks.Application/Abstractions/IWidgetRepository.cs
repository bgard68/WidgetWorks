using WidgetWorks.Domain.Catalog;

namespace WidgetWorks.Application.Abstractions;

/// <summary>Filter/paging criteria for catalog listing and search.</summary>
/// <summary>
/// A catalogue listing request. Search and Category are independent narrowings combined with AND,
/// so "turbine" within Mega means both, not either.
/// </summary>
/// <param name="Sort">
/// One of <see cref="WidgetSort"/>. Never interpolated into SQL - the repository maps it through a
/// fixed set of order-by clauses, so an unrecognised value falls back to the default rather than
/// reaching the database.
/// </param>
public sealed record WidgetQuery(
    string? Search,
    bool ActiveOnly,
    int Page,
    int PageSize,
    string? Category = null,
    string? Sort = null)
{
    public int Offset => (Math.Max(1, Page) - 1) * PageSize;
}

/// <summary>The orderings the catalogue offers. Values are part of the API contract.</summary>
public static class WidgetSort
{
    public const string Featured = "featured";
    public const string PriceAscending = "price-asc";
    public const string PriceDescending = "price-desc";
    public const string Name = "name";
}

public interface IWidgetRepository
{
    Task<Widget?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Widget?> GetBySkuAsync(string normalizedSku, CancellationToken ct);

    Task<IReadOnlyList<Widget>> SearchAsync(WidgetQuery query, CancellationToken ct);

    Task<int> CountAsync(WidgetQuery query, CancellationToken ct);

    Task AddAsync(Widget widget, CancellationToken ct);

    /// <summary>
    /// Saves the editable description of a widget: name, description, image, price and whether it
    /// is listed. Deliberately writes nothing about stock. The previous single writer put every
    /// column in the statement, so a caller that had read the row a moment earlier wrote its stale
    /// quantity_reserved back over a reservation taken in between - an oversell caused by an edit.
    /// </summary>
    Task UpdateDetailsAsync(Widget widget, CancellationToken ct);

    /// <summary>
    /// Applies a signed delta to on-hand stock and returns the resulting availability, or null when
    /// the row no longer satisfies the rules - archived, gone, or the delta would take on-hand below
    /// zero or below what is already reserved.
    ///
    /// The arithmetic and both guards run inside the statement rather than in the caller, so a
    /// reservation taken between reading and writing is accounted for instead of overwritten, and
    /// two concurrent adjustments both count.
    /// </summary>
    Task<int?> AdjustStockAsync(Guid id, int delta, DateTimeOffset now, CancellationToken ct);

    /// <summary>Retires a widget from sale, keeping the row so past orders still resolve.</summary>
    Task ArchiveAsync(Guid id, DateTimeOffset archivedAt, CancellationToken ct);

    /// <summary>How many order lines reference this widget — zero means it can be deleted outright.</summary>
    Task<int> CountOrderLinesAsync(Guid widgetId, CancellationToken ct);

    /// <summary>Permanently removes a widget. Only safe when it has no order history.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);
}
