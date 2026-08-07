using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class OrderRepository(IDbConnectionFactory factory) : IOrderRepository
{
    private const string OrderColumns =
        "id, order_number, user_id, email, ship_name, ship_line1, ship_line2, ship_city, ship_state, ship_postal_code, ship_country, subtotal, shipping_method, shipping, tax_state, tax_rate, tax, total, status, payment_provider, payment_reference, created_at, updated_at";

    private const string ItemColumns =
        "id, order_id, widget_id, sku, name, unit_price, quantity, line_subtotal";

    private const string InsertOrderSql =
        @"insert into orders (id, order_number, user_id, email, ship_name, ship_line1, ship_line2, ship_city, ship_state, ship_postal_code, ship_country, subtotal, shipping_method, shipping, tax_state, tax_rate, tax, total, status, payment_provider, payment_reference, created_at, updated_at)
          values (@Id, @OrderNumber, @UserId, @Email, @ShipName, @ShipLine1, @ShipLine2, @ShipCity, @ShipState, @ShipPostalCode, @ShipCountry, @Subtotal, @ShippingMethod, @Shipping, @TaxState, @TaxRate, @Tax, @Total, @Status, @PaymentProvider, @PaymentReference, @CreatedAt, @UpdatedAt)";

    private const string InsertItemSql =
        @"insert into order_items (id, order_id, widget_id, sku, name, unit_price, quantity, line_subtotal)
          values (@Id, @OrderId, @WidgetId, @Sku, @Name, @UnitPrice, @Quantity, @LineSubtotal)";

    private const string ReserveSql =
        @"update widgets set quantity_reserved = quantity_reserved + @Quantity, updated_at = @Now
          where id = @WidgetId and (quantity_on_hand - quantity_reserved) >= @Quantity";

    public async Task<bool> TryPlaceAsync(Order order, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        using var tx = db.BeginTransaction();
        try
        {
            await db.ExecuteAsync(new CommandDefinition(InsertOrderSql, order, tx, cancellationToken: ct));

            foreach (var item in order.Items)
            {
                var itemParam = new { item.Id, OrderId = order.Id, item.WidgetId, item.Sku, item.Name, item.UnitPrice, item.Quantity, item.LineSubtotal };
                await db.ExecuteAsync(new CommandDefinition(InsertItemSql, itemParam, tx, cancellationToken: ct));
            }

            foreach (var item in order.Items)
            {
                var reserveParam = new { item.WidgetId, item.Quantity, Now = order.CreatedAt };
                var affected = await db.ExecuteAsync(new CommandDefinition(ReserveSql, reserveParam, tx, cancellationToken: ct));
                if (affected != 1)
                {
                    tx.Rollback();
                    return false;
                }
            }

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task MarkPaidAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "update orders set status = @Status, payment_provider = @Provider, payment_reference = @Reference, updated_at = @Now where id = @Id",
            new { Id = orderId, Status = OrderStatus.Paid, Provider = provider, Reference = reference, Now = now });
    }

    public async Task MarkPaymentFailedAsync(Order order, string reason, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        using var tx = db.BeginTransaction();
        try
        {
            await db.ExecuteAsync(new CommandDefinition(
                "update orders set status = @Status, updated_at = @Now where id = @Id",
                new { Id = order.Id, Status = OrderStatus.PaymentFailed, Now = now }, tx, cancellationToken: ct));

            foreach (var item in order.Items)
            {
                await db.ExecuteAsync(new CommandDefinition(
                    "update widgets set quantity_reserved = quantity_reserved - @Quantity, updated_at = @Now where id = @WidgetId and quantity_reserved >= @Quantity",
                    new { item.WidgetId, item.Quantity, Now = now }, tx, cancellationToken: ct));
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var order = await db.QuerySingleOrDefaultAsync<Order>(
            $"select {OrderColumns} from orders where id = @id", new { id });
        if (order is null)
        {
            return null;
        }

        order.Items = (await db.QueryAsync<OrderItem>(
            $"select {ItemColumns} from order_items where order_id = @id order by name", new { id })).ToList();
        return order;
    }

    public async Task<Order?> GetByNumberAndEmailAsync(string orderNumber, string email, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var order = await db.QuerySingleOrDefaultAsync<Order>(
            $"select {OrderColumns} from orders where order_number = @orderNumber and lower(email) = lower(@email)",
            new { orderNumber, email });
        if (order is null)
        {
            return null;
        }

        order.Items = (await db.QueryAsync<OrderItem>(
            $"select {ItemColumns} from order_items where order_id = @id order by name", new { id = order.Id })).ToList();
        return order;
    }

    public async Task<IReadOnlyList<Order>> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var list = (await db.QueryAsync<Order>(
            $"select {OrderColumns} from orders where user_id = @userId order by created_at desc", new { userId })).ToList();
        if (list.Count == 0)
        {
            return list;
        }

        var ids = list.Select(o => o.Id).ToArray();
        var items = await db.QueryAsync<OrderItem>(
            $"select {ItemColumns} from order_items where order_id = any(@ids)", new { ids });
        var grouped = items.GroupBy(i => i.OrderId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var o in list)
        {
            o.Items = grouped.TryGetValue(o.Id, out var it) ? it : [];
        }

        return list;
    }
}
