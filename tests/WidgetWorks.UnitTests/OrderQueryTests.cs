using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Carts.GetCart;
using WidgetWorks.Application.Carts.RemoveItem;
using WidgetWorks.Application.Orders;
using WidgetWorks.Application.Orders.Admin;
using WidgetWorks.Application.Orders.GetMine;
using WidgetWorks.Application.Orders.ListMine;
using WidgetWorks.Application.Orders.ListRecent;
using WidgetWorks.Application.Orders.Lookup;
using WidgetWorks.Application.TwoFactor.Enroll;
using WidgetWorks.Domain.Carts;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Read-side handlers and the projections they return. The case that matters beyond mapping is
/// ownership: "my order" must be unreachable by anyone else's id, and a guest lookup must require
/// the email that placed it.
/// </summary>
public class OrderQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private static Order MakeOrder(Guid? userId, string email = "jane@example.com", string number = "WW-20260501-ABC123",
        string status = OrderStatus.Paid, DateTimeOffset? createdAt = null, params (string Sku, int Qty, decimal Price)[] lines)
    {
        var items = (lines.Length == 0 ? [("WW-1", 2, 12.50m)] : lines)
            .Select(l => new OrderItem
            {
                Id = Guid.NewGuid(),
                WidgetId = Guid.NewGuid(),
                Sku = l.Item1,
                Name = "Widget " + l.Item1,
                UnitPrice = l.Item3,
                Quantity = l.Item2,
                LineSubtotal = l.Item3 * l.Item2,
            })
            .ToList();

        return new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = number,
            UserId = userId,
            Email = email,
            Subtotal = items.Sum(i => i.LineSubtotal),
            ShippingMethod = "Standard",
            Shipping = 6.99m,
            TaxState = "CA",
            TaxRate = 0.0725m,
            Tax = 1.81m,
            Total = items.Sum(i => i.LineSubtotal) + 6.99m + 1.81m,
            Status = status,
            PaymentProvider = "Mock",
            PaymentReference = "mock_ref_1",
            TrackingNumber = "1Z999AA10123456784",
            CreatedAt = createdAt ?? Now,
            UpdatedAt = createdAt ?? Now,
            Items = items,
        };
    }

    private static InMemoryOrderRepository Repo(params Order[] orders)
    {
        var repo = new InMemoryOrderRepository(new InMemoryWidgetRepository());
        repo.Orders.AddRange(orders);
        return repo;
    }

    // ---- my orders -----------------------------------------------------------------------

    [Fact]
    public async Task My_order_returns_the_full_view()
    {
        var userId = Guid.NewGuid();
        var order = MakeOrder(userId);
        var result = await new GetMyOrderHandler(Repo(order)).Handle(new GetMyOrderQuery(userId, order.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var view = result.Value!;
        Assert.Equal(order.OrderNumber, view.OrderNumber);
        Assert.Equal("CA", view.TaxState);
        Assert.Equal(0.0725m, view.TaxRate);
        Assert.Equal(order.Total, view.Total);
        Assert.Equal("1Z999AA10123456784", view.TrackingNumber);
        Assert.Single(view.Items);
    }

    [Fact]
    public async Task My_order_refuses_to_return_someone_elses_order()
    {
        var owner = Guid.NewGuid();
        var order = MakeOrder(owner);

        var result = await new GetMyOrderHandler(Repo(order))
            .Handle(new GetMyOrderQuery(Guid.NewGuid(), order.Id), CancellationToken.None);

        // Same wording as a missing order: knowing an id exists is itself a leak.
        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
    }

    [Fact]
    public async Task My_order_refuses_a_guest_order_that_has_no_owner()
    {
        var order = MakeOrder(userId: null);
        var result = await new GetMyOrderHandler(Repo(order))
            .Handle(new GetMyOrderQuery(Guid.NewGuid(), order.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task My_order_fails_for_an_id_that_does_not_exist()
    {
        var result = await new GetMyOrderHandler(Repo())
            .Handle(new GetMyOrderQuery(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
    }

    [Fact]
    public async Task My_orders_lists_only_mine_newest_first()
    {
        var me = Guid.NewGuid();
        var older = MakeOrder(me, number: "WW-1", createdAt: Now.AddDays(-2));
        var newer = MakeOrder(me, number: "WW-2", createdAt: Now);
        var theirs = MakeOrder(Guid.NewGuid(), number: "WW-3");

        var list = await new ListMyOrdersHandler(Repo(older, newer, theirs))
            .Handle(new ListMyOrdersQuery(me), CancellationToken.None);

        Assert.Equal(["WW-2", "WW-1"], list.Select(o => o.OrderNumber));
    }

    [Fact]
    public async Task My_orders_is_empty_for_someone_with_no_orders()
    {
        var list = await new ListMyOrdersHandler(Repo(MakeOrder(Guid.NewGuid())))
            .Handle(new ListMyOrdersQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(list);
    }

    // ---- admin ---------------------------------------------------------------------------

    [Fact]
    public async Task Admin_can_open_any_order_regardless_of_owner()
    {
        var order = MakeOrder(Guid.NewGuid());
        var result = await new GetOrderByIdHandler(Repo(order)).Handle(new GetOrderByIdQuery(order.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(order.OrderNumber, result.Value!.OrderNumber);
    }

    [Fact]
    public async Task Admin_order_lookup_fails_for_an_unknown_id()
    {
        var result = await new GetOrderByIdHandler(Repo()).Handle(new GetOrderByIdQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
    }

    [Fact]
    public async Task Recent_orders_carries_the_item_count_from_the_lines()
    {
        var order = MakeOrder(Guid.NewGuid(), lines: [("A", 2, 5m), ("B", 3, 5m)]);

        var list = await new ListRecentOrdersHandler(Repo(order)).Handle(new ListRecentOrdersQuery(50), CancellationToken.None);

        // Regression: an "optimization" once skipped loading item rows, and every row showed 0.
        Assert.Equal(5, Assert.Single(list).ItemCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5000)]
    public async Task Recent_orders_clamps_a_nonsense_limit_to_the_default(int limit)
    {
        var repo = Repo(Enumerable.Range(0, 60).Select(i => MakeOrder(Guid.NewGuid(), number: $"WW-{i}")).ToArray());

        var list = await new ListRecentOrdersHandler(repo).Handle(new ListRecentOrdersQuery(limit), CancellationToken.None);

        Assert.Equal(50, list.Count);
    }

    [Fact]
    public async Task Recent_orders_honours_a_sensible_limit()
    {
        var repo = Repo(Enumerable.Range(0, 10).Select(i => MakeOrder(Guid.NewGuid(), number: $"WW-{i}")).ToArray());

        var list = await new ListRecentOrdersHandler(repo).Handle(new ListRecentOrdersQuery(3), CancellationToken.None);

        Assert.Equal(3, list.Count);
    }

    // ---- guest lookup --------------------------------------------------------------------

    [Theory]
    [InlineData("", "jane@example.com")]
    [InlineData("WW-1", "")]
    [InlineData("   ", "   ")]
    public async Task Guest_lookup_requires_both_fields(string number, string email)
    {
        var result = await new GuestOrderLookupHandler(Repo())
            .Handle(new GuestOrderLookupQuery(number, email), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order number and email are required.", result.Error);
    }

    [Fact]
    public async Task Guest_lookup_finds_an_order_by_number_and_email()
    {
        var order = MakeOrder(userId: null, email: "guest@example.com", number: "WW-20260501-XYZ999");

        var result = await new GuestOrderLookupHandler(Repo(order))
            .Handle(new GuestOrderLookupQuery("  WW-20260501-XYZ999  ", "  guest@example.com  "), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(order.Id, result.Value!.Id);
    }

    [Fact]
    public async Task Guest_lookup_with_the_wrong_email_finds_nothing()
    {
        var order = MakeOrder(userId: null, email: "guest@example.com");

        var result = await new GuestOrderLookupHandler(Repo(order))
            .Handle(new GuestOrderLookupQuery(order.OrderNumber, "someone-else@example.com"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
    }

    // ---- projections ---------------------------------------------------------------------

    [Fact]
    public void OrderView_copies_every_money_field_and_all_lines()
    {
        var order = MakeOrder(Guid.NewGuid(), lines: [("A", 1, 10m), ("B", 4, 2.50m)]);

        var view = OrderView.From(order);

        Assert.Equal(order.Subtotal, view.Subtotal);
        Assert.Equal(order.Shipping, view.Shipping);
        Assert.Equal(order.Tax, view.Tax);
        Assert.Equal(order.Total, view.Total);
        Assert.Equal(order.ShippingMethod, view.ShippingMethod);
        Assert.Equal(order.PaymentProvider, view.PaymentProvider);
        Assert.Equal(order.PaymentReference, view.PaymentReference);
        Assert.Equal(order.CreatedAt, view.CreatedAt);
        Assert.Equal(2, view.Items.Count);
        Assert.Equal(10m, view.Items[0].LineSubtotal);
        Assert.Equal(10m, view.Items[1].LineSubtotal);
    }

    [Fact]
    public void OrderSummary_counts_units_not_lines()
    {
        var order = MakeOrder(Guid.NewGuid(), lines: [("A", 2, 5m), ("B", 3, 5m)]);

        var summary = OrderSummary.From(order);

        Assert.Equal(5, summary.ItemCount);
        Assert.Equal(order.Total, summary.Total);
        Assert.Equal(order.Status, summary.Status);
    }

    [Fact]
    public void OrderView_of_an_order_with_no_tracking_leaves_it_null()
    {
        var order = MakeOrder(Guid.NewGuid());
        order.TrackingNumber = null;

        Assert.Null(OrderView.From(order).TrackingNumber);
    }

    // ---- cart reads ----------------------------------------------------------------------

    private sealed record CartCtx(InMemoryCartRepository Carts, InMemoryWidgetRepository Widgets, Cart Cart, Widget Widget);

    private static CartCtx CartSetup()
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = "WW-1",
            Name = "Standard Widget",
            Price = 12.50m,
            QuantityOnHand = 10,
            IsActive = true,
        };
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = new Cart { Id = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        cart.Items.Add(new CartItem { CartId = cart.Id, WidgetId = widget.Id, Quantity = 2 });
        carts.Store[cart.Id] = cart;

        return new CartCtx(carts, widgets, cart, widget);
    }

    [Fact]
    public async Task Get_cart_prices_the_lines()
    {
        var c = CartSetup();
        var result = await new GetCartHandler(c.Carts, c.Widgets).Handle(new GetCartQuery(c.Cart.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.ItemCount);
        Assert.Equal(25.00m, result.Value.Subtotal);
    }

    [Fact]
    public async Task Get_cart_fails_for_an_unknown_cart()
    {
        var c = CartSetup();
        var result = await new GetCartHandler(c.Carts, c.Widgets).Handle(new GetCartQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Cart not found.", result.Error);
    }

    [Fact]
    public async Task Removing_an_item_empties_the_cart_and_touches_it()
    {
        var c = CartSetup();
        var clock = new FakeTimeProvider(Now.AddHours(1));

        var result = await new RemoveCartItemHandler(c.Carts, c.Widgets, clock)
            .Handle(new RemoveCartItemCommand(c.Cart.Id, c.Widget.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.ItemCount);
        Assert.Empty(result.Value.Items);
        Assert.Equal(Now.AddHours(1), c.Carts.Store[c.Cart.Id].UpdatedAt);
    }

    [Fact]
    public async Task Removing_an_item_that_is_not_in_the_cart_is_a_no_op()
    {
        var c = CartSetup();

        var result = await new RemoveCartItemHandler(c.Carts, c.Widgets, new FakeTimeProvider(Now))
            .Handle(new RemoveCartItemCommand(c.Cart.Id, Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.ItemCount);
    }

    [Fact]
    public async Task Removing_from_an_unknown_cart_fails()
    {
        var c = CartSetup();

        var result = await new RemoveCartItemHandler(c.Carts, c.Widgets, new FakeTimeProvider(Now))
            .Handle(new RemoveCartItemCommand(Guid.NewGuid(), c.Widget.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Cart not found.", result.Error);
    }

    // ---- 2FA enrollment start ------------------------------------------------------------

    [Fact]
    public async Task Enroll_stores_a_pending_secret_and_returns_the_otpauth_uri()
    {
        var users = new InMemoryUserRepository();
        var user = new User { Id = Guid.NewGuid(), Email = "jane@example.com", NormalizedEmail = "JANE@EXAMPLE.COM" };
        users.Store[user.Id] = user;
        var twoFactor = new InMemoryTwoFactorRepository();

        var result = await new EnrollHandler(users, twoFactor, new FakeTotpService())
            .Handle(new EnrollCommand(user.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("SECRETBASE32", result.Value!.SecretBase32);
        Assert.StartsWith("otpauth://", result.Value.OtpAuthUri);

        // Pending, not confirmed: the code still has to be proven.
        Assert.False(twoFactor.Secrets[user.Id].IsConfirmed);
    }

    [Fact]
    public async Task Enroll_fails_for_an_unknown_user()
    {
        var result = await new EnrollHandler(new InMemoryUserRepository(), new InMemoryTwoFactorRepository(), new FakeTotpService())
            .Handle(new EnrollCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("User not found.", result.Error);
    }
}
