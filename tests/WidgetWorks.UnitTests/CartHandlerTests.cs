using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Carts.AddItem;
using WidgetWorks.Application.Carts.Merge;
using WidgetWorks.Application.Carts.UpdateItem;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class CartHandlerTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static (InMemoryCartRepository Carts, InMemoryWidgetRepository Widgets, Widget Widget) Setup(int available = 10, decimal price = 5m)
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = "WW-1",
            Name = "Gizmo",
            IsActive = true,
            Price = price,
            QuantityOnHand = available,
            QuantityReserved = 0,
        };
        widgets.Store[widget.Id] = widget;
        return (new InMemoryCartRepository(), widgets, widget);
    }

    [Fact]
    public async Task Add_creates_cart_and_prices_line()
    {
        var (carts, widgets, widget) = Setup();
        var handler = new AddCartItemHandler(carts, widgets, Clock());

        var result = await handler.Handle(new AddCartItemCommand(null, null, widget.Id, 2), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);
        Assert.Equal(10m, result.Value!.Subtotal);
        Assert.Equal(2, result.Value!.ItemCount);
    }

    [Fact]
    public async Task Add_caps_quantity_at_available()
    {
        var (carts, widgets, widget) = Setup(available: 3);
        var handler = new AddCartItemHandler(carts, widgets, Clock());

        var result = await handler.Handle(new AddCartItemCommand(null, null, widget.Id, 99), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.Items[0].Quantity);
    }

    [Fact]
    public async Task Add_twice_accumulates_on_same_cart()
    {
        var (carts, widgets, widget) = Setup(available: 10);
        var handler = new AddCartItemHandler(carts, widgets, Clock());

        var first = await handler.Handle(new AddCartItemCommand(null, null, widget.Id, 2), CancellationToken.None);
        var second = await handler.Handle(new AddCartItemCommand(first.Value!.Id, null, widget.Id, 3), CancellationToken.None);

        Assert.Equal(5, second.Value!.Items[0].Quantity);
    }

    [Fact]
    public async Task Update_to_zero_removes_line()
    {
        var (carts, widgets, widget) = Setup();
        var add = new AddCartItemHandler(carts, widgets, Clock());
        var created = await add.Handle(new AddCartItemCommand(null, null, widget.Id, 2), CancellationToken.None);

        var update = new UpdateCartItemHandler(carts, widgets, Clock());
        var result = await update.Handle(new UpdateCartItemCommand(created.Value!.Id, widget.Id, 0, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
    }

    [Fact]
    public async Task Merge_combines_guest_into_user_cart_and_discards_guest()
    {
        var (carts, widgets, widget) = Setup(available: 10);
        var add = new AddCartItemHandler(carts, widgets, Clock());
        var userId = Guid.NewGuid();

        await add.Handle(new AddCartItemCommand(null, userId, widget.Id, 1), CancellationToken.None);
        var guest = await add.Handle(new AddCartItemCommand(null, null, widget.Id, 2), CancellationToken.None);

        var merge = new MergeCartHandler(carts, widgets, Clock());
        var result = await merge.Handle(new MergeCartCommand(userId, guest.Value!.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.Items[0].Quantity);
        Assert.Null(await carts.GetAsync(guest.Value!.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Add_requires_a_positive_quantity()
    {
        var (carts, widgets, widget) = Setup();
        var handler = new AddCartItemHandler(carts, widgets, Clock());

        var result = await handler.Handle(new AddCartItemCommand(null, null, widget.Id, 0), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Quantity must be at least 1.", result.Error);
        Assert.Empty(carts.Store);   // no cart was created for a refused add
    }

    [Fact]
    public async Task Add_of_an_unknown_or_hidden_widget_is_refused()
    {
        var (carts, widgets, widget) = Setup();
        widget.IsActive = false;
        var handler = new AddCartItemHandler(carts, widgets, Clock());

        var unknown = await handler.Handle(new AddCartItemCommand(null, null, Guid.NewGuid(), 1), CancellationToken.None);
        var hidden = await handler.Handle(new AddCartItemCommand(null, null, widget.Id, 1), CancellationToken.None);

        Assert.Equal("Widget not found.", unknown.Error);
        Assert.Equal("Widget not found.", hidden.Error);
    }

    [Fact]
    public async Task Add_of_a_sold_out_widget_is_refused()
    {
        var (carts, widgets, widget) = Setup(available: 5);
        widget.QuantityReserved = 5;   // everything on hand belongs to someone else's order
        var handler = new AddCartItemHandler(carts, widgets, Clock());

        var result = await handler.Handle(new AddCartItemCommand(null, null, widget.Id, 1), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("This widget is out of stock.", result.Error);
    }

    [Fact]
    public async Task Add_without_a_cart_id_finds_the_users_existing_cart()
    {
        var (carts, widgets, widget) = Setup();
        var handler = new AddCartItemHandler(carts, widgets, Clock());
        var userId = Guid.NewGuid();

        var first = await handler.Handle(new AddCartItemCommand(null, userId, widget.Id, 1), CancellationToken.None);
        // Same user, no cart id (e.g. a second browser tab): the add must land in the same cart.
        var second = await handler.Handle(new AddCartItemCommand(null, userId, widget.Id, 2), CancellationToken.None);

        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Equal(userId, second.Value!.UserId);
        Assert.Equal(3, second.Value!.Items.Single().Quantity);
        // The line reports how much more a shopper could still take.
        Assert.Equal(10, second.Value!.Items.Single().QuantityAvailable);
    }

    [Fact]
    public async Task Merge_of_an_unknown_guest_cart_fails()
    {
        var (carts, widgets, _) = Setup();
        var merge = new MergeCartHandler(carts, widgets, Clock());

        var result = await merge.Handle(new MergeCartCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Guest cart not found.", result.Error);
    }

    [Fact]
    public async Task Merge_never_absorbs_another_users_cart()
    {
        var (carts, widgets, widget) = Setup();
        var add = new AddCartItemHandler(carts, widgets, Clock());
        var victim = Guid.NewGuid();
        var victimCart = await add.Handle(new AddCartItemCommand(null, victim, widget.Id, 2), CancellationToken.None);

        var merge = new MergeCartHandler(carts, widgets, Clock());
        var result = await merge.Handle(new MergeCartCommand(Guid.NewGuid(), victimCart.Value!.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart not found.", result.Error);
        Assert.NotNull(await carts.GetAsync(victimCart.Value!.Id, CancellationToken.None));   // untouched
    }

    [Fact]
    public async Task An_absurd_quantity_clamps_to_stock_instead_of_wrapping_negative()
    {
        var widgets = new InMemoryWidgetRepository();
        var carts = new InMemoryCartRepository();
        var widgetId = Guid.NewGuid();
        widgets.Store[widgetId] = new Widget
        {
            Id = widgetId,
            Sku = "WW-001",
            Name = "Standard Widget Block Cobalt",
            Price = 9.99m,
            IsActive = true,
            QuantityOnHand = 5,
            QuantityReserved = 0,
        };

        var handler = new AddCartItemHandler(carts, widgets, new FakeTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var result = await handler.Handle(
            new AddCartItemCommand(null, null, widgetId, int.MaxValue), CancellationToken.None);

        // In int arithmetic this wrapped negative and answered "out of stock". The clamp should
        // clamp: five are available, so five is what lands in the cart.
        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value!.Items.Single().Quantity);
    }
}
