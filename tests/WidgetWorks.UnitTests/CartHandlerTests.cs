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
        var result = await update.Handle(new UpdateCartItemCommand(created.Value!.Id, widget.Id, 0), CancellationToken.None);

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
}
