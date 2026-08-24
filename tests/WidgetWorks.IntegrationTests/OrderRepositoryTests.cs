using Dapper;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// The order repository against real PostgreSQL. The reservation is the reason this suite exists:
/// stock is committed by a conditional UPDATE inside a transaction, so overselling is prevented by
/// the database, not by application code. No in-memory fake can prove that — only concurrent
/// connections against a real server can.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OrderRepositoryTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private OrderRepository Orders => new(db.Connections);

    private WidgetRepository Widgets => new(db.Connections);

    private async Task<Widget> GivenWidget(int onHand)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = "IT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Name = "Widget " + Guid.NewGuid().ToString("N")[..6],
            Description = "Integration fixture.",
            Price = 10m,
            QuantityOnHand = onHand,
            QuantityReserved = 0,
            IsActive = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await Widgets.AddAsync(widget, CancellationToken.None);
        return widget;
    }

    /// <summary>orders.user_id is a real foreign key, so an owner has to exist first.</summary>
    private async Task<Guid> GivenUser()
    {
        var id = Guid.NewGuid();
        var email = $"it-{id:N}@example.com";
        await new UserRepository(db.Connections).AddAsync(
            new User
            {
                Id = id,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                PasswordHash = "hash",
                Role = UserRoles.Customer,
                SecurityStamp = Guid.NewGuid(),
                CreatedAt = Now,
            },
            CancellationToken.None);
        return id;
    }

    private static Order OrderFor(Widget widget, int quantity, string? number = null) => new()
    {
        Id = Guid.NewGuid(),
        OrderNumber = number ?? "WW-IT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        Email = "jane@example.com",
        ShipName = "Jane Doe",
        ShipLine1 = "1 Main St",
        ShipCity = "Springfield",
        ShipState = "CA",
        ShipPostalCode = "90210",
        ShipCountry = "US",
        Subtotal = widget.Price * quantity,
        ShippingMethod = "Standard",
        Shipping = 6.99m,
        TaxState = "CA",
        TaxRate = 0.0725m,
        Tax = 1.45m,
        Total = (widget.Price * quantity) + 6.99m + 1.45m,
        Status = OrderStatus.Pending,
        CreatedAt = Now,
        UpdatedAt = Now,
        Items =
        [
            new OrderItem
            {
                Id = Guid.NewGuid(),
                WidgetId = widget.Id,
                Sku = widget.Sku,
                Name = widget.Name,
                UnitPrice = widget.Price,
                Quantity = quantity,
                LineSubtotal = widget.Price * quantity,
            },
        ],
    };

    [Fact]
    public async Task Placing_an_order_reserves_the_stock()
    {
        var widget = await GivenWidget(onHand: 5);

        var placed = await Orders.TryPlaceAsync(OrderFor(widget, 2), CancellationToken.None);

        Assert.True(placed);
        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(2, after!.QuantityReserved);
        Assert.Equal(5, after.QuantityOnHand);
        Assert.Equal(3, after.QuantityAvailable);
    }

    [Fact]
    public async Task An_order_for_more_than_is_available_is_refused_and_reserves_nothing()
    {
        var widget = await GivenWidget(onHand: 1);

        var placed = await Orders.TryPlaceAsync(OrderFor(widget, 2), CancellationToken.None);

        Assert.False(placed);
        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(0, after!.QuantityReserved);
    }

    [Fact]
    public async Task A_refused_order_leaves_no_row_behind()
    {
        var widget = await GivenWidget(onHand: 0);
        var order = OrderFor(widget, 1);

        await Orders.TryPlaceAsync(order, CancellationToken.None);

        // The whole placement is one transaction: a failed reservation must roll the order back too.
        Assert.Null(await Orders.GetByIdAsync(order.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_orders_cannot_oversell_the_last_units()
    {
        var widget = await GivenWidget(onHand: 10);

        // Ten buyers, two units each, ten in stock: exactly five can win.
        var attempts = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => new OrderRepository(db.Connections)
                .TryPlaceAsync(OrderFor(widget, 2), CancellationToken.None)))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Equal(5, results.Count(placed => placed));
        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(10, after!.QuantityReserved);
        Assert.Equal(0, after.QuantityAvailable);
    }

    [Fact]
    public async Task Marking_paid_records_the_provider_and_reference()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 1);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        await Orders.MarkPaidAsync(order.Id, "Mock", "mock_ref_1", Now, CancellationToken.None);

        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.Paid, stored!.Status);
        Assert.Equal("Mock", stored.PaymentProvider);
        Assert.Equal("mock_ref_1", stored.PaymentReference);
    }

    [Fact]
    public async Task A_declined_payment_releases_the_reservation()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 3);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        await Orders.MarkPaymentFailedAsync(order, "Card declined.", Now, CancellationToken.None);

        // Stock a customer never paid for must go back on the shelf.
        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(0, after!.QuantityReserved);
        Assert.Equal(5, after.QuantityAvailable);
        Assert.Equal(OrderStatus.PaymentFailed, (await Orders.GetByIdAsync(order.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task An_awaiting_payment_order_keeps_its_reservation()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        await Orders.MarkAwaitingPaymentAsync(order.Id, "Klarna", "klarna_1", Now, CancellationToken.None);

        // The stock stays committed while the provider settles, or it could be sold twice.
        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(2, after!.QuantityReserved);
        Assert.Equal(OrderStatus.AwaitingPayment, (await Orders.GetByIdAsync(order.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task An_order_can_be_found_by_its_payment_reference()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 1);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkAwaitingPaymentAsync(order.Id, "Klarna", "klarna_lookup", Now, CancellationToken.None);

        // This is how a webhook correlates an inbound event back to an order.
        var found = await Orders.GetByPaymentReferenceAsync("Klarna", "klarna_lookup", CancellationToken.None);

        Assert.Equal(order.Id, found!.Id);
        Assert.Null(await Orders.GetByPaymentReferenceAsync("Klarna", "not-a-reference", CancellationToken.None));
        Assert.Null(await Orders.GetByPaymentReferenceAsync("Stripe", "klarna_lookup", CancellationToken.None));
    }

    [Fact]
    public async Task A_guest_can_look_an_order_up_only_with_the_email_that_placed_it()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 1);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        Assert.NotNull(await Orders.GetByNumberAndEmailAsync(order.OrderNumber, "jane@example.com", CancellationToken.None));
        Assert.Null(await Orders.GetByNumberAndEmailAsync(order.OrderNumber, "someone@else.com", CancellationToken.None));
    }

    [Fact]
    public async Task Updating_status_stores_the_tracking_number()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 1);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaidAsync(order.Id, "Mock", "r", Now, CancellationToken.None);

        await Orders.UpdateStatusAsync(order.Id, OrderStatus.Shipped, "1Z-TRACK", Now.AddHours(1), CancellationToken.None);

        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.Shipped, stored!.Status);
        Assert.Equal("1Z-TRACK", stored.TrackingNumber);
    }

    [Fact]
    public async Task A_users_orders_come_back_newest_first_with_their_lines()
    {
        var userId = await GivenUser();
        var widget = await GivenWidget(onHand: 20);

        var older = OrderFor(widget, 1);
        older.UserId = userId;
        older.CreatedAt = Now.AddDays(-3);
        await Orders.TryPlaceAsync(older, CancellationToken.None);

        var newer = OrderFor(widget, 2);
        newer.UserId = userId;
        newer.CreatedAt = Now;
        await Orders.TryPlaceAsync(newer, CancellationToken.None);

        var mine = await Orders.GetForUserAsync(userId, CancellationToken.None);

        Assert.Equal([newer.Id, older.Id], mine.Select(o => o.Id));
        Assert.All(mine, o => Assert.NotEmpty(o.Items));
    }

    [Fact]
    public async Task Another_users_orders_are_not_returned()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 1);
        order.UserId = await GivenUser();
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        Assert.Empty(await Orders.GetForUserAsync(await GivenUser(), CancellationToken.None));
    }

    [Fact]
    public async Task The_recent_list_carries_item_rows_so_counts_are_right()
    {
        var widget = await GivenWidget(onHand: 20);
        var order = OrderFor(widget, 4);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        var recent = await Orders.GetRecentAsync(50, CancellationToken.None);

        // The bug this guards: skipping the item rows made every order report 0 items.
        var mine = recent.Single(o => o.Id == order.Id);
        Assert.Equal(4, mine.UnitCount);
    }

    [Fact]
    public async Task The_recent_list_honours_its_limit()
    {
        var widget = await GivenWidget(onHand: 50);
        for (var i = 0; i < 4; i++)
        {
            await Orders.TryPlaceAsync(OrderFor(widget, 1), CancellationToken.None);
        }

        Assert.Equal(2, (await Orders.GetRecentAsync(2, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task An_unknown_order_id_returns_null_rather_than_throwing()
    {
        Assert.Null(await Orders.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task A_mid_transaction_failure_rolls_the_whole_order_back()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 1);
        // A second line for a widget that does not exist: the order row inserts, then the item
        // insert violates the foreign key -- everything must unwind, including the first insert.
        order.Items.Add(new OrderItem
        {
            Id = Guid.NewGuid(),
            WidgetId = Guid.NewGuid(),
            Sku = "GHOST",
            Name = "Ghost",
            UnitPrice = 1m,
            Quantity = 1,
            LineSubtotal = 1m,
        });

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Orders.TryPlaceAsync(order, CancellationToken.None));

        Assert.Null(await Orders.GetByIdAsync(order.Id, CancellationToken.None));   // no half-written order
        Assert.Equal(0, (await Widgets.GetByIdAsync(widget.Id, CancellationToken.None))!.QuantityReserved);
    }

    [Fact]
    public async Task A_failure_while_releasing_a_reservation_leaves_the_order_untouched()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 2);
        Assert.True(await Orders.TryPlaceAsync(order, CancellationToken.None));

        // Cancel the token the moment the connection is open: the first statement inside the
        // transaction fails, and the rollback must leave both the order and the reservation as
        // they were.
        var cts = new CancellationTokenSource();
        var flaky = new OrderRepository(new CancelAfterOpenFactory(db.Connections, cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => flaky.MarkPaymentFailedAsync(order, "card declined", Now, cts.Token));

        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.Pending, stored!.Status);
        Assert.Equal(2, (await Widgets.GetByIdAsync(widget.Id, CancellationToken.None))!.QuantityReserved);
    }

    [Fact]
    public async Task The_recent_list_is_empty_when_there_are_no_orders()
    {
        // Every test cleans up after itself, but be explicit: this asserts the zero-orders
        // shortcut, so clear whatever the rest of the suite left behind.
        using var connection = await db.Connections.OpenAsync(CancellationToken.None);
        await connection.ExecuteAsync("delete from order_items; delete from orders");

        Assert.Empty(await Orders.GetRecentAsync(10, CancellationToken.None));
    }

    private sealed class CancelAfterOpenFactory(IDbConnectionFactory inner, CancellationTokenSource cts) : IDbConnectionFactory
    {
        public async Task<System.Data.IDbConnection> OpenAsync(CancellationToken ct)
        {
            var connection = await inner.OpenAsync(ct);
            cts.Cancel();
            return connection;
        }
    }
}
