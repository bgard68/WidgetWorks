using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Catalog;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class WidgetRepository(IDbConnectionFactory factory) : IWidgetRepository
{
    private const string Columns =
        "id, sku, name, description, image_url, price, is_active, quantity_on_hand, quantity_reserved, created_at, updated_at, archived_at";

    // Archived widgets are excluded from every listing. GetByIdAsync deliberately
    // still returns them so order history and reporting can resolve a retired widget.
    private const string Filter =
        @"where archived_at is null
            and (@ActiveOnly = false or is_active = true)
            and (@Search is null or name ilike @Pattern or sku ilike @Pattern)
            and (@Category is null or name ilike @CategoryPattern or sku ilike @CategoryPattern)";

    /// <summary>
    /// Sort clauses, chosen by key rather than built from the caller's string - the only safe way to
    /// put user input near an order-by. An unknown key sorts by name, which is the catalogue default.
    /// Every clause ends with name so paging is stable when prices tie; without a total ordering the
    /// same row can appear on two pages.
    /// </summary>
    private static string OrderBy(string? sort) => sort switch
    {
        WidgetSort.PriceAscending => "order by price asc, name asc",
        WidgetSort.PriceDescending => "order by price desc, name asc",
        WidgetSort.Name => "order by name asc",

        // Featured leads with what can actually be bought and pushes sold-out items to the end.
        // This used to happen in the browser over one page, which quietly meant "in stock on this
        // page first"; done here it holds across the whole result set.
        _ => "order by ((quantity_on_hand - quantity_reserved) > 0) desc, name asc",
    };

    /// <summary>Parameters shared by the listing and its count, so the two can never diverge.</summary>
    private static object FilterParameters(WidgetQuery query) => new
    {
        query.ActiveOnly,
        query.Search,
        Pattern = query.Search is null ? null : $"%{query.Search}%",
        query.Category,
        CategoryPattern = query.Category is null ? null : $"%{query.Category}%",
        Limit = query.PageSize,
        query.Offset,
    };

    public async Task<Widget?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<Widget>(
            $"select {Columns} from widgets where id = @id",
            new { id });
    }

    public async Task<Widget?> GetBySkuAsync(string normalizedSku, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<Widget>(
            $"select {Columns} from widgets where sku = @normalizedSku",
            new { normalizedSku });
    }

    public async Task<IReadOnlyList<Widget>> SearchAsync(WidgetQuery query, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var rows = await db.QueryAsync<Widget>(new CommandDefinition(
            $@"select {Columns} from widgets
               {Filter}
               {OrderBy(query.Sort)}
               limit @Limit offset @Offset",
            FilterParameters(query),
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> CountAsync(WidgetQuery query, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            $@"select count(*) from widgets
               {Filter}",
            FilterParameters(query),
            cancellationToken: ct));
    }

    public async Task AddAsync(Widget widget, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into widgets (id, sku, name, description, image_url, price, is_active, quantity_on_hand, quantity_reserved, created_at, updated_at)
              values (@Id, @Sku, @Name, @Description, @ImageUrl, @Price, @IsActive, @QuantityOnHand, @QuantityReserved, @CreatedAt, @UpdatedAt)",
            widget);
    }

    public async Task UpdateDetailsAsync(Widget widget, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition(
            @"update widgets set name = @Name, description = @Description, image_url = @ImageUrl,
                 price = @Price, is_active = @IsActive, updated_at = @UpdatedAt
              where id = @Id",
            widget, cancellationToken: ct));
    }

    public async Task<int?> AdjustStockAsync(Guid id, int delta, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);

        // Both guards are predicates on the row's current value, not on a copy the caller read
        // earlier, so a reservation taken in between is respected rather than trampled. RETURNING
        // hands back the availability the change actually produced.
        return await db.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"update widgets
                 set quantity_on_hand = quantity_on_hand + @Delta, updated_at = @Now
               where id = @Id
                 and archived_at is null
                 and quantity_on_hand + @Delta >= 0
                 and quantity_on_hand + @Delta >= quantity_reserved
               returning quantity_on_hand - quantity_reserved",
            new { Id = id, Delta = delta, Now = now }, cancellationToken: ct));
    }

    public async Task ArchiveAsync(Guid id, DateTimeOffset archivedAt, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition(
            @"update widgets set is_active = false, archived_at = @ArchivedAt, updated_at = @ArchivedAt
              where id = @Id",
            new { Id = id, ArchivedAt = archivedAt }, cancellationToken: ct));
    }

    public async Task<int> CountOrderLinesAsync(Guid widgetId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.ExecuteScalarAsync<int>(
            "select count(*) from order_items where widget_id = @widgetId",
            new { widgetId });
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync("delete from widgets where id = @id", new { id });
    }
}
