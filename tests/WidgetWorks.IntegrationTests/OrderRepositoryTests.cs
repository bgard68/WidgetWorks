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

        // Set directly rather than via TransitionTo: MarkPaidAsync moved the row, not this
        // in-memory instance, so the entity would refuse Pending -> Shipped.
        order.Status = OrderStatus.Shipped;
        order.TrackingNumber = "1Z-TRACK";
        await Orders.UpdateStatusAsync(order, Now.AddHours(1), CancellationToken.None);

        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.Shipped, stored!.Status);
        Assert.Equal("1Z-TRACK", stored.TrackingNumber);
    }

    [Fact]
    public async Task A_repeated_payment_failure_releases_the_reservation_only_once()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, 3);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        var first = await Orders.MarkPaymentFailedAsync(order, "declined", Now, CancellationToken.None);
        var second = await Orders.MarkPaymentFailedAsync(order, "declined", Now, CancellationToken.None);

        Assert.True(first);
        // The row, not the caller, decides. A redelivered webhook is declined rather than
        // decrementing quantity_reserved a second time and eating another order's stock.
        Assert.False(second);

        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(10, after!.QuantityOnHand);
        Assert.Equal(0, after.QuantityReserved);
    }

    [Fact]
    public async Task A_late_settlement_cannot_overwrite_an_order_that_already_failed()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaymentFailedAsync(order, "declined", Now, CancellationToken.None);

        var settled = await Orders.MarkPaidAsync(order.Id, "Mock", "ref-1", Now.AddMinutes(1), CancellationToken.None);

        // Marking it paid here would claim money for an order whose stock is already back on sale.
        Assert.False(settled);
        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.PaymentFailed, stored!.Status);
    }

    [Fact]
    public async Task A_settled_order_cannot_be_failed_by_a_stale_event()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaidAsync(order.Id, "Mock", "ref-2", Now, CancellationToken.None);

        var failed = await Orders.MarkPaymentFailedAsync(order, "stale", Now.AddMinutes(1), CancellationToken.None);

        Assert.False(failed);
        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.Paid, stored!.Status);
        // The reservation must survive: the goods are sold and still owed to this order.
        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(2, after!.QuantityReserved);
    }

    [Fact]
    public async Task Shipping_turns_the_reservation_into_a_real_decrement()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        var reserved = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(5, reserved!.QuantityOnHand);
        Assert.Equal(2, reserved.QuantityReserved);

        order.Status = OrderStatus.Shipped;
        await Orders.UpdateStatusAsync(order, Now.AddHours(1), CancellationToken.None);

        // The goods left the shelf: on-hand falls and the hold is gone, so availability
        // (on_hand - reserved) is unchanged at 3 while on-hand now tells the truth.
        var shipped = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(3, shipped!.QuantityOnHand);
        Assert.Equal(0, shipped.QuantityReserved);
    }

    [Fact]
    public async Task Cancelling_hands_the_reservation_back()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        order.Status = OrderStatus.Cancelled;
        await Orders.UpdateStatusAsync(order, Now.AddHours(1), CancellationToken.None);

        // Nothing shipped, so the stock returns to sale in full.
        var cancelled = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(5, cancelled!.QuantityOnHand);
        Assert.Equal(0, cancelled.QuantityReserved);
    }

    [Fact]
    public async Task Delivery_moves_no_stock_because_shipping_already_did()
    {
        var widget = await GivenWidget(onHand: 5);
        var order = OrderFor(widget, 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        order.Status = OrderStatus.Shipped;
        await Orders.UpdateStatusAsync(order, Now.AddHours(1), CancellationToken.None);
        order.Status = OrderStatus.Delivered;
        await Orders.UpdateStatusAsync(order, Now.AddHours(2), CancellationToken.None);

        var delivered = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(3, delivered!.QuantityOnHand);
        Assert.Equal(0, delivered.QuantityReserved);
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

    [Fact]
    public async Task An_unconfirmed_charge_is_hidden_from_the_expiry_sweep_until_it_is_resolved()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, quantity: 2);
        Assert.True(await Orders.TryPlaceAsync(order, CancellationToken.None));

        // The provider never said what became of the charge.
        Assert.True(await Orders.MarkPaymentUnconfirmedAsync(order.Id, "Stripe", Now, CancellationToken.None));

        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.AwaitingPayment, stored!.Status);
        Assert.NotNull(stored.PaymentUnconfirmedAt);
        Assert.Null(stored.PaymentReference);   // there was none to record

        // The sweep that releases abandoned reservations must not see it. This is the assertion the
        // whole design rests on: failing this order on a timer would release the stock of a charge
        // that may well have been taken.
        var stale = await Orders.GetStaleAwaitingPaymentAsync(Now.AddDays(1), 100, CancellationToken.None);
        Assert.DoesNotContain(stale, o => o.Id == order.Id);

        // Reconciliation does see it, with items loaded so a confirmed refusal can release stock.
        var unconfirmed = await Orders.GetUnconfirmedPaymentsAsync(100, CancellationToken.None);
        var found = Assert.Single(unconfirmed, o => o.Id == order.Id);
        Assert.NotEmpty(found.Items);

        // Recording the reference discovered by a probe hands the order back to the ordinary paths:
        // the webhook can correlate on it, and the sweep can see it again.
        Assert.True(await Orders.RecordPaymentReferenceAsync(order.Id, "Stripe", "pi_probed", Now, CancellationToken.None));

        var handedBack = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal("pi_probed", handedBack!.PaymentReference);
        Assert.Null(handedBack.PaymentUnconfirmedAt);
        Assert.Contains(
            await Orders.GetStaleAwaitingPaymentAsync(Now.AddDays(1), 100, CancellationToken.None),
            o => o.Id == order.Id);
    }

    [Fact]
    public async Task Settling_an_unconfirmed_order_clears_the_mark_that_exempted_it()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, quantity: 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaymentUnconfirmedAsync(order.Id, "Stripe", Now, CancellationToken.None);

        // The reconciled truth: it had been paid all along.
        Assert.True(await Orders.MarkPaidAsync(order.Id, "Stripe", "pi_reconciled", Now, CancellationToken.None));

        var paid = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.Paid, paid!.Status);
        Assert.Equal("pi_reconciled", paid.PaymentReference);

        // Cleared, so the next reconciliation pass does not pick up an order that is already decided.
        Assert.Null(paid.PaymentUnconfirmedAt);
        Assert.DoesNotContain(
            await Orders.GetUnconfirmedPaymentsAsync(100, CancellationToken.None),
            o => o.Id == order.Id);
    }

    [Fact]
    public async Task An_order_that_has_moved_on_cannot_be_marked_unconfirmed()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, quantity: 2);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaidAsync(order.Id, "Stripe", "pi_settled", Now, CancellationToken.None);

        // Compare-and-set, as everywhere else on this path: a late writer cannot drag a decided order
        // back into "we don't know".
        Assert.False(await Orders.MarkPaymentUnconfirmedAsync(order.Id, "Stripe", Now, CancellationToken.None));
        Assert.Equal(OrderStatus.Paid, (await Orders.GetByIdAsync(order.Id, CancellationToken.None))!.Status);
    }

    /// <summary>An order for the given customer and total, created at a given moment.</summary>
    private async Task<Order> GivenOrderAt(Widget widget, string email, decimal total, DateTimeOffset at, string status = OrderStatus.Paid)
    {
        var order = OrderFor(widget, quantity: 1);
        order.Email = email;
        order.Total = total;
        order.CreatedAt = at;
        order.UpdatedAt = at;
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        if (status == OrderStatus.Paid)
        {
            await Orders.MarkPaidAsync(order.Id, "Mock", "pi_" + Guid.NewGuid().ToString("N")[..8], at, CancellationToken.None);
        }
        else if (status == OrderStatus.PaymentFailed)
        {
            await Orders.MarkPaymentFailedAsync(order, "declined", at, CancellationToken.None);
        }

        return order;
    }

    [Fact]
    public async Task Possible_duplicates_pair_only_live_orders_close_together_for_the_same_customer_and_total()
    {
        var widget = await GivenWidget(onHand: 200);
        var email = $"dup-{Guid.NewGuid():N}@example.com";

        var first = await GivenOrderAt(widget, email, 50m, Now);
        var second = await GivenOrderAt(widget, email, 50m, Now.AddMinutes(2));

        // Same customer and total but hours later: a customer, not a mistake.
        var muchLater = await GivenOrderAt(widget, email, 50m, Now.AddHours(5));

        // Same window, different total.
        var differentTotal = await GivenOrderAt(widget, email, 75m, Now.AddMinutes(1));

        // Same window and total, different customer.
        var otherCustomer = await GivenOrderAt(widget, $"other-{Guid.NewGuid():N}@example.com", 50m, Now.AddMinutes(1));

        // A retry after a decline — the system working, and nobody charged twice.
        var declined = await GivenOrderAt(widget, email, 90m, Now, status: OrderStatus.PaymentFailed);
        var retried = await GivenOrderAt(widget, email, 90m, Now.AddMinutes(1));

        var flagged = await Orders.GetPossibleDuplicatesAsync(TimeSpan.FromMinutes(10), 100, CancellationToken.None);
        var ids = flagged.Select(o => o.Id).ToList();

        // Both halves of the real pair, because staff compare them to decide which one to refund.
        Assert.Contains(first.Id, ids);
        Assert.Contains(second.Id, ids);

        Assert.DoesNotContain(muchLater.Id, ids);
        Assert.DoesNotContain(differentTotal.Id, ids);
        Assert.DoesNotContain(otherCustomer.Id, ids);
        Assert.DoesNotContain(declined.Id, ids);
        Assert.DoesNotContain(retried.Id, ids);

        // Items are loaded, so the list can show what was ordered without a second round trip.
        Assert.NotEmpty(flagged.First(o => o.Id == first.Id).Items);
    }

    [Fact]
    public async Task A_refund_hands_the_reservation_back_because_nothing_had_shipped()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, quantity: 3);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaidAsync(order.Id, "Mock", "pi_refundable", Now, CancellationToken.None);

        order.Status = OrderStatus.Refunded;
        await Orders.UpdateStatusAsync(order, Now, CancellationToken.None);

        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);

        // Reserved falls, on-hand does not: the goods never left the shelf, so they go back on sale.
        Assert.Equal(0, after!.QuantityReserved);
        Assert.Equal(10, after.QuantityOnHand);
        Assert.Equal(OrderStatus.Refunded, (await Orders.GetByIdAsync(order.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_full_refund_releases_the_stock_and_a_partial_one_does_not()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, quantity: 3);
        order.Total = 100m;
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaidAsync(order.Id, "Mock", "pi_refundable", Now, CancellationToken.None);

        // Part of it: the customer is still owed goods, so the units stay committed.
        Assert.True(await Orders.RecordRefundAsync(order.Id, 40m, fullyRefunded: false, Now, CancellationToken.None));

        var partial = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(40m, partial!.RefundedTotal);
        Assert.Equal(OrderStatus.Paid, partial.Status);
        Assert.Equal(3, (await Widgets.GetByIdAsync(widget.Id, CancellationToken.None))!.QuantityReserved);

        // The rest: nothing is owed now, so the goods go back on sale.
        Assert.True(await Orders.RecordRefundAsync(order.Id, 100m, fullyRefunded: true, Now, CancellationToken.None));

        var full = await Orders.GetByIdAsync(order.Id, CancellationToken.None);
        Assert.Equal(OrderStatus.Refunded, full!.Status);
        Assert.Equal(100m, full.RefundedTotal);

        var after = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(0, after!.QuantityReserved);
        Assert.Equal(10, after.QuantityOnHand);   // nothing shipped, so on-hand is untouched
    }

    [Fact]
    public async Task Two_refunds_racing_cannot_both_apply_their_amount()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, quantity: 1);
        order.Total = 100m;
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkPaidAsync(order.Id, "Mock", "pi_raced", Now, CancellationToken.None);

        // Both read a refunded_total of zero and both try to write 60. Overpaying by 20 is what the
        // guard exists to stop, and only a real server deciding between two connections can prove it.
        var results = await Task.WhenAll(
            Task.Run(() => Orders.RecordRefundAsync(order.Id, 60m, false, Now, CancellationToken.None)),
            Task.Run(() => Orders.RecordRefundAsync(order.Id, 60m, false, Now, CancellationToken.None)));

        Assert.Equal(1, results.Count(applied => applied));
        Assert.Equal(60m, (await Orders.GetByIdAsync(order.Id, CancellationToken.None))!.RefundedTotal);
    }

    [Fact]
    public async Task An_order_that_is_no_longer_paid_cannot_be_refunded()
    {
        var widget = await GivenWidget(onHand: 10);
        var order = OrderFor(widget, quantity: 1);
        await Orders.TryPlaceAsync(order, CancellationToken.None);

        // Still Pending: nothing has been charged, so there is nothing to give back.
        Assert.False(await Orders.RecordRefundAsync(order.Id, 10m, false, Now, CancellationToken.None));
        Assert.Equal(0m, (await Orders.GetByIdAsync(order.Id, CancellationToken.None))!.RefundedTotal);
    }
}
