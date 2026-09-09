using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// The query the reservation sweep runs, against real PostgreSQL.
///
/// The sweep's policy is proven against a fake in the unit suite; what a fake cannot prove is that
/// the SQL underneath it selects the same rows. This is the only thing standing between an abandoned
/// checkout and stock held forever, and every part of it is a place to be wrong: the status filter,
/// the strict &lt; on the cutoff, the oldest-first ordering that decides who gets released when a
/// backlog exceeds one batch, and the per-order item load the release itself depends on.
///
/// Every order here is stamped in a 2019 window no other suite uses, so the assertions hold whatever
/// else has been written to the shared database and in whatever order the suites run.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StaleReservationQueryTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Cutoff = new(2019, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private OrderRepository Orders => new(db.Connections);

    private WidgetRepository Widgets => new(db.Connections);

    private async Task<Widget> GivenWidget(int onHand)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = "SW-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Name = "Widget " + Guid.NewGuid().ToString("N")[..6],
            Description = "Stale-sweep fixture.",
            Price = 10m,
            QuantityOnHand = onHand,
            QuantityReserved = 0,
            IsActive = true,
            CreatedAt = Cutoff,
            UpdatedAt = Cutoff,
        };
        await Widgets.AddAsync(widget, CancellationToken.None);
        return widget;
    }

    private static Order OrderFor(Widget widget, int quantity) => new()
    {
        Id = Guid.NewGuid(),
        OrderNumber = "WW-SW-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
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
        CreatedAt = Cutoff,
        UpdatedAt = Cutoff,
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

    /// <summary>Places an order holding stock and parks it in AwaitingPayment as of <paramref name="updatedAt"/>.</summary>
    private async Task<Order> GivenOrderAwaitingPaymentSince(Widget widget, int quantity, DateTimeOffset updatedAt)
    {
        var order = OrderFor(widget, quantity);
        await Orders.TryPlaceAsync(order, CancellationToken.None);
        await Orders.MarkAwaitingPaymentAsync(
            order.Id, "Mock", "pi_" + order.OrderNumber, updatedAt, CancellationToken.None);
        return order;
    }

    [Fact]
    public async Task GetStaleAwaitingPayment_SeveralOrdersPastTheCutoff_ReturnsOldestFirstWithItemsLoaded()
    {
        // Arrange — two abandoned checkouts, the second parked a day after the first.
        var widget = await GivenWidget(onHand: 20);
        var oldest = await GivenOrderAwaitingPaymentSince(widget, 2, new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = await GivenOrderAwaitingPaymentSince(widget, 3, new DateTimeOffset(2019, 1, 2, 0, 0, 0, TimeSpan.Zero));

        // Act
        var stale = await Orders.GetStaleAwaitingPaymentAsync(Cutoff, limit: 50, CancellationToken.None);

        // Assert — oldest first, because a backlog longer than one batch must drain in the order it
        // formed rather than starving the orders that have waited longest.
        //
        // Narrowed to this test's own two orders because the cutoff is open-ended backwards: every
        // order any other test in this collection parked earlier also qualifies. Filtering preserves
        // the query's sequence, so this still proves the ordering the sweep depends on.
        var ours = new[] { oldest.OrderNumber, newer.OrderNumber };
        var mine = stale.Where(o => ours.Contains(o.OrderNumber)).ToList();
        Assert.Equal([oldest.OrderNumber, newer.OrderNumber], mine.Select(o => o.OrderNumber));

        // The release hands stock back line by line, so an order arriving without its items would
        // silently release nothing while still being counted as swept.
        var loaded = mine[0];
        Assert.Equal(widget.Id, Assert.Single(loaded.Items).WidgetId);
        Assert.Equal(2, loaded.Items[0].Quantity);
        Assert.Equal(widget.Sku, loaded.Items[0].Sku);
        Assert.Equal(OrderStatus.AwaitingPayment, loaded.Status);
    }

    [Fact]
    public async Task GetStaleAwaitingPayment_OrdersInsideTheWindowOrAlreadySettled_AreLeftAlone()
    {
        // Arrange — one genuinely abandoned order, against the two kinds of order that must never be
        // swept: one still inside its payment window, and one whose webhook already landed.
        var widget = await GivenWidget(onHand: 20);
        var abandoned = await GivenOrderAwaitingPaymentSince(widget, 2, new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var stillWaiting = await GivenOrderAwaitingPaymentSince(widget, 2, new DateTimeOffset(2019, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var settled = await GivenOrderAwaitingPaymentSince(widget, 2, new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Orders.MarkPaidAsync(
            settled.Id, "Mock", "pi_" + settled.OrderNumber, new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        // Act
        var stale = await Orders.GetStaleAwaitingPaymentAsync(Cutoff, limit: 50, CancellationToken.None);

        // Assert — sweeping either of the others would release stock a customer has paid for, or is
        // still in the middle of paying for.
        var numbers = stale.Select(o => o.OrderNumber).ToList();
        Assert.Contains(abandoned.OrderNumber, numbers);
        Assert.DoesNotContain(stillWaiting.OrderNumber, numbers);
        Assert.DoesNotContain(settled.OrderNumber, numbers);
    }

    [Fact]
    public async Task GetStaleAwaitingPayment_MoreStaleOrdersThanTheLimit_ReturnsOnlyTheOldestBatch()
    {
        // Arrange — three abandoned orders and a batch size of two, the case the BatchSize option
        // exists for: a bad day must not turn one sweep into a long transaction storm.
        //
        // Stamped in 2001, older than anything any other suite writes, so "the oldest two" is these
        // and the limit can be asserted on the query's own result rather than a filtered copy.
        var widget = await GivenWidget(onHand: 20);
        var first = await GivenOrderAwaitingPaymentSince(widget, 1, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var second = await GivenOrderAwaitingPaymentSince(widget, 1, new DateTimeOffset(2001, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var third = await GivenOrderAwaitingPaymentSince(widget, 1, new DateTimeOffset(2001, 1, 3, 0, 0, 0, TimeSpan.Zero));

        // Act
        var stale = await Orders.GetStaleAwaitingPaymentAsync(
            new DateTimeOffset(2001, 6, 1, 0, 0, 0, TimeSpan.Zero), limit: 2, CancellationToken.None);

        // Assert — the limit takes the oldest two and leaves the third for the next pass.
        Assert.Equal([first.OrderNumber, second.OrderNumber], stale.Select(o => o.OrderNumber));
        Assert.DoesNotContain(third.OrderNumber, stale.Select(o => o.OrderNumber));
    }
}
