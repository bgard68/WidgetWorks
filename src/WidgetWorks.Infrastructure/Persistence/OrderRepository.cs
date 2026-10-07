using System.Data;
using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class OrderRepository(IDbConnectionFactory factory) : IOrderRepository
{
    private const string OrderColumns =
        "id, order_number, user_id, email, ship_name, ship_line1, ship_line2, ship_city, ship_state, ship_postal_code, ship_country, subtotal, shipping_method, shipping, tax_state, tax_rate, tax, total, status, payment_provider, payment_reference, tracking_number, payment_unconfirmed_at, refunded_total, created_at, updated_at";

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

    /// <summary>Hands a reservation back: the goods never left, so only the hold is undone.</summary>
    private const string ReleaseSql =
        @"update widgets set quantity_reserved = quantity_reserved - @Quantity, updated_at = @Now
          where id = @WidgetId and quantity_reserved >= @Quantity";

    /// <summary>
    /// Turns a reservation into a real decrement when the parcel leaves. Both columns fall by the
    /// same amount, so availability (on_hand - reserved) is unchanged and the on-hand figure starts
    /// telling the truth about what is on the shelf. The guards keep either column off negative.
    /// </summary>
    private const string ShipSql =
        @"update widgets set quantity_on_hand = quantity_on_hand - @Quantity,
                             quantity_reserved = quantity_reserved - @Quantity,
                             updated_at = @Now
          where id = @WidgetId and quantity_reserved >= @Quantity and quantity_on_hand >= @Quantity";

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

    /// <summary>
    /// The statuses a settlement outcome may still be applied from. Anything else means the order
    /// has already moved on and a late or repeated event must not touch it.
    /// </summary>
    private static readonly string[] AwaitingSettlement = [OrderStatus.Pending, OrderStatus.AwaitingPayment];

    public async Task<bool> MarkAwaitingPaymentAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var affected = await db.ExecuteAsync(new CommandDefinition(
            @"update orders set status = @Status, payment_provider = @Provider, payment_reference = @Reference, updated_at = @Now
              where id = @Id and status = @Expected",
            new { Id = orderId, Status = OrderStatus.AwaitingPayment, Provider = provider, Reference = reference, Now = now, Expected = OrderStatus.Pending },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<bool> MarkPaymentUnconfirmedAsync(Guid orderId, string provider, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);

        // No payment_reference is written because there is none to write — the whole point is that we
        // never learned the provider's id for this charge. Reconciliation fills it in once it finds out.
        var affected = await db.ExecuteAsync(new CommandDefinition(
            @"update orders set status = @Status, payment_provider = @Provider, payment_unconfirmed_at = @Now, updated_at = @Now
              where id = @Id and status = @Expected",
            new { Id = orderId, Status = OrderStatus.AwaitingPayment, Provider = provider, Now = now, Expected = OrderStatus.Pending },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<bool> RecordPaymentReferenceAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var affected = await db.ExecuteAsync(new CommandDefinition(
            @"update orders set payment_provider = @Provider, payment_reference = @Reference,
                     payment_unconfirmed_at = null, updated_at = @Now
              where id = @Id and status = @Expected",
            new { Id = orderId, Provider = provider, Reference = reference, Now = now, Expected = OrderStatus.AwaitingPayment },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<IReadOnlyList<Order>> GetUnconfirmedPaymentsAsync(int limit, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var orders = (await db.QueryAsync<Order>(new CommandDefinition(
            $@"select {OrderColumns} from orders
               where payment_unconfirmed_at is not null
               order by payment_unconfirmed_at
               limit @Limit",
            new { Limit = limit },
            cancellationToken: ct))).ToList();

        await LoadItemsAsync(db, orders, ct);
        return orders;
    }

    public async Task<bool> MarkPaidAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var affected = await db.ExecuteAsync(new CommandDefinition(
            @"update orders set status = @Status, payment_provider = @Provider, payment_reference = @Reference,
                     payment_unconfirmed_at = null, updated_at = @Now
              where id = @Id and status = any(@Expected)",
            new { Id = orderId, Status = OrderStatus.Paid, Provider = provider, Reference = reference, Now = now, Expected = AwaitingSettlement },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<bool> MarkPaymentFailedAsync(Order order, string reason, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        using var tx = db.BeginTransaction();
        try
        {
            // Compare-and-set inside the transaction, so the row decides who wins rather than the
            // caller. Two concurrent deliveries of the same failure both pass an application-level
            // status check; only one can win this update, and only the winner releases the
            // reservation. Without it a redelivered webhook decrements quantity_reserved a second
            // time and eats stock still held by a different order.
            var applied = await db.ExecuteAsync(new CommandDefinition(
                @"update orders set status = @Status, payment_unconfirmed_at = null, updated_at = @Now
                  where id = @Id and status = any(@Expected)",
                new { Id = order.Id, Status = OrderStatus.PaymentFailed, Now = now, Expected = AwaitingSettlement },
                tx, cancellationToken: ct));

            if (applied != 1)
            {
                tx.Rollback();
                return false;
            }

            foreach (var item in order.Items)
            {
                await db.ExecuteAsync(new CommandDefinition(
                    ReleaseSql, new { item.WidgetId, item.Quantity, Now = now }, tx, cancellationToken: ct));
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

    public async Task UpdateStatusAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        // Status and stock move together or not at all. Splitting them would let a crash between
        // the two leave a shipped order whose goods are still reserved, which is exactly the drift
        // this method exists to stop.
        using var db = await factory.OpenAsync(ct);
        using var tx = db.BeginTransaction();
        try
        {
            await db.ExecuteAsync(new CommandDefinition(
                "update orders set status = @Status, tracking_number = @Tracking, updated_at = @Now where id = @Id",
                new { Id = order.Id, Status = order.Status, Tracking = order.TrackingNumber, Now = now },
                tx, cancellationToken: ct));

            // Shipping turns a reservation into a real decrement; cancelling hands it back.
            // Delivered moves no stock - shipping already did.
            // Explicit string? rather than var: a switch expression mixing string arms with a
            // null arm has no best common type to infer.
            string? sql = order.Status switch
            {
                OrderStatus.Shipped => ShipSql,

                // Both hand the hold back. A refund is only reachable from Paid — before anything
                // ships — so the goods are still on the shelf and the units go back on sale.
                OrderStatus.Cancelled or OrderStatus.Refunded => ReleaseSql,
                _ => null,
            };

            if (sql is not null)
            {
                foreach (var item in order.Items)
                {
                    await db.ExecuteAsync(new CommandDefinition(
                        sql, new { item.WidgetId, item.Quantity, Now = now }, tx, cancellationToken: ct));
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<Order>> GetStaleAwaitingPaymentAsync(DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var orders = (await db.QueryAsync<Order>(new CommandDefinition(
            $@"select {OrderColumns} from orders
               where status = @Status and updated_at < @Cutoff and payment_unconfirmed_at is null
               order by updated_at
               limit @Limit",
            new { Status = OrderStatus.AwaitingPayment, Cutoff = cutoff, Limit = limit },
            cancellationToken: ct))).ToList();

        await LoadItemsAsync(db, orders, ct);
        return orders;
    }

    /// <summary>
    /// Fills in the line items for a batch of orders. Both sweeps need them — releasing a reservation
    /// is per-item — and both ran the same loop, so it lives in one place.
    /// </summary>
    private async Task LoadItemsAsync(IDbConnection db, List<Order> orders, CancellationToken ct)
    {
        foreach (var order in orders)
        {
            order.Items = (await db.QueryAsync<OrderItem>(new CommandDefinition(
                $"select {ItemColumns} from order_items where order_id = @id order by name",
                new { id = order.Id }, cancellationToken: ct))).ToList();
        }
    }

    public async Task<bool> RecordRefundAsync(Guid orderId, decimal refundedTotal, bool fullyRefunded, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        using var tx = db.BeginTransaction();
        try
        {
            // Guarded on Paid and on the running total, so two staff refunding at once cannot both
            // apply their amount: the second sees a refunded_total that no longer matches what it read
            // and is declined rather than overpaying.
            var status = fullyRefunded ? OrderStatus.Refunded : OrderStatus.Paid;
            var applied = await db.ExecuteAsync(new CommandDefinition(
                @"update orders set refunded_total = @RefundedTotal, status = @Status, updated_at = @Now
                  where id = @Id and status = @Expected and refunded_total < @RefundedTotal",
                new { Id = orderId, RefundedTotal = refundedTotal, Status = status, Now = now, Expected = OrderStatus.Paid },
                tx, cancellationToken: ct));

            if (applied != 1)
            {
                tx.Rollback();
                return false;
            }

            // Only a fully refunded order hands its stock back. A part refund leaves goods still owed,
            // so the units stay committed to it.
            if (fullyRefunded)
            {
                var items = (await db.QueryAsync<OrderItem>(new CommandDefinition(
                    $"select {ItemColumns} from order_items where order_id = @id",
                    new { id = orderId }, tx, cancellationToken: ct))).ToList();

                foreach (var item in items)
                {
                    await db.ExecuteAsync(new CommandDefinition(
                        ReleaseSql, new { item.WidgetId, item.Quantity, Now = now }, tx, cancellationToken: ct));
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

    public async Task<IReadOnlyList<Order>> GetPossibleDuplicatesAsync(TimeSpan window, int limit, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);

        // Self-join rather than a window function on purpose: "another order exists that matches on
        // customer and total and sits within the window" is the definition, and EXISTS says exactly
        // that. Failed and cancelled orders are excluded on both sides — a retry after a decline is
        // the system working, not a duplicate.
        //
        // Deliberately a heuristic, and deliberately not acted on automatically. Two identical orders
        // minutes apart are usually a mistake and occasionally a customer who meant it, and no query
        // can tell those apart. It produces a list for a human, nothing more.
        var orders = (await db.QueryAsync<Order>(new CommandDefinition(
            $@"select {OrderColumns} from orders o
               where o.status <> all(@Excluded)
                 and exists (
                   select 1 from orders d
                   where d.id <> o.id
                     and d.email = o.email
                     and d.total = o.total
                     and d.status <> all(@Excluded)
                     and d.created_at between o.created_at - @Window::interval and o.created_at + @Window::interval)
               order by o.email, o.created_at desc
               limit @Limit",
            new
            {
                Excluded = new[] { OrderStatus.PaymentFailed, OrderStatus.Cancelled },
                Window = $"{window.TotalSeconds} seconds",
                Limit = limit,
            },
            cancellationToken: ct))).ToList();

        await LoadItemsAsync(db, orders, ct);
        return orders;
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

    public async Task<Order?> GetByPaymentReferenceAsync(string provider, string reference, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var order = await db.QuerySingleOrDefaultAsync<Order>(
            $"select {OrderColumns} from orders where payment_provider = @provider and payment_reference = @reference",
            new { provider, reference });
        if (order is null)
        {
            return null;
        }

        order.Items = (await db.QueryAsync<OrderItem>(
            $"select {ItemColumns} from order_items where order_id = @id order by name", new { id = order.Id })).ToList();
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

    public async Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var list = (await db.QueryAsync<Order>(
            $"select {OrderColumns} from orders order by created_at desc limit @limit",
            new { limit })).ToList();
        if (list.Count == 0)
        {
            return list;
        }

        // The item rows are loaded, not skipped: OrderSummary derives its item count from them,
        // so leaving Items empty reported every order as containing nothing.
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
