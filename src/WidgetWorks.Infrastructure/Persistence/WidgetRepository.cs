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
            and (@Search is null or name ilike @Pattern or sku ilike @Pattern)";

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
        var rows = await db.QueryAsync<Widget>(
            $@"select {Columns} from widgets
               {Filter}
               order by name
               limit @Limit offset @Offset",
            new
            {
                query.ActiveOnly,
                query.Search,
                Pattern = query.Search is null ? null : $"%{query.Search}%",
                Limit = query.PageSize,
                query.Offset,
            });
        return rows.ToList();
    }

    public async Task<int> CountAsync(WidgetQuery query, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.ExecuteScalarAsync<int>(
            $@"select count(*) from widgets
               {Filter}",
            new
            {
                query.ActiveOnly,
                query.Search,
                Pattern = query.Search is null ? null : $"%{query.Search}%",
            });
    }

    public async Task AddAsync(Widget widget, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into widgets (id, sku, name, description, image_url, price, is_active, quantity_on_hand, quantity_reserved, created_at, updated_at)
              values (@Id, @Sku, @Name, @Description, @ImageUrl, @Price, @IsActive, @QuantityOnHand, @QuantityReserved, @CreatedAt, @UpdatedAt)",
            widget);
    }

    public async Task UpdateAsync(Widget widget, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"update widgets set name = @Name, description = @Description, image_url = @ImageUrl, price = @Price,
                 is_active = @IsActive, quantity_on_hand = @QuantityOnHand, quantity_reserved = @QuantityReserved,
                 updated_at = @UpdatedAt, archived_at = @ArchivedAt
              where id = @Id",
            widget);
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
