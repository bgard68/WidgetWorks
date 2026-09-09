using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Carts.GetCart;
using WidgetWorks.Application.Carts.Merge;
using WidgetWorks.Application.Carts.RemoveItem;
using WidgetWorks.Application.Carts.UpdateItem;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// What a cart does when the world moves underneath it: another user reaches for it, or the
/// catalogue stops selling something already in it.
///
/// Carts outlive the catalogue rows they point at — a basket can sit for days while a widget is
/// hidden, sold out or deleted — so every one of these is an ordinary Tuesday rather than a corner
/// case. The rule throughout is that the cart degrades to what is still sellable instead of failing,
/// and that an unauthorized caller learns nothing about whether the cart exists.
/// </summary>
public class CartResilienceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An earlier stamp, so a handler that touches the cart when it should not is visible.</summary>
    private static readonly DateTimeOffset Stamped = Now.AddMinutes(-5);

    private static Widget Sellable(string sku, string name, decimal price) => new()
    {
        Id = Guid.NewGuid(),
        Sku = sku,
        Name = name,
        Price = price,
        IsActive = true,
        QuantityOnHand = 20,
        QuantityReserved = 0,
    };

    [Fact]
    public async Task RemoveItem_RequestedBySomeoneOtherThanTheOwner_ReportsNotFoundAndLeavesTheLineIntact()
    {
        // Arrange — a cart claimed by one shopper, and a second shopper who has guessed its id.
        var widgets = new InMemoryWidgetRepository();
        var widget = Sellable("WW-001", "Standard Widget Block Cobalt", 9.99m);
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(Guid.NewGuid(), CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, widget.Id, 2, Stamped, CancellationToken.None);
        await carts.TouchAsync(cart.Id, Stamped, CancellationToken.None);

        var handler = new RemoveCartItemHandler(carts, widgets, new FakeTimeProvider(Now));

        // Act
        var result = await handler.Handle(
            new RemoveCartItemCommand(cart.Id, widget.Id, Guid.NewGuid()), CancellationToken.None);

        // Assert — the same answer a missing cart gets, so the id cannot be confirmed by guessing,
        // and the owner's line is untouched down to the timestamp.
        Assert.True(result.IsFailure);
        Assert.Equal("Cart not found.", result.Error);
        Assert.Equal(2, Assert.Single(carts.Store[cart.Id].Items).Quantity);
        Assert.Equal(Stamped, carts.Store[cart.Id].UpdatedAt);
    }

    [Fact]
    public async Task UpdateItem_RequestedBySomeoneOtherThanTheOwner_ReportsNotFoundAndLeavesTheQuantityIntact()
    {
        // Arrange — the same guessed id, this time aimed at rewriting a quantity rather than deleting it.
        var widgets = new InMemoryWidgetRepository();
        var widget = Sellable("WW-001", "Standard Widget Block Cobalt", 9.99m);
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(Guid.NewGuid(), CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, widget.Id, 2, Stamped, CancellationToken.None);
        await carts.TouchAsync(cart.Id, Stamped, CancellationToken.None);

        var handler = new UpdateCartItemHandler(carts, widgets, new FakeTimeProvider(Now));

        // Act
        var result = await handler.Handle(
            new UpdateCartItemCommand(cart.Id, widget.Id, 7, Guid.NewGuid()), CancellationToken.None);

        // Assert — refused before the quantity branch is even reached, so neither the line nor the
        // stamp moves.
        Assert.True(result.IsFailure);
        Assert.Equal("Cart not found.", result.Error);
        Assert.Equal(2, Assert.Single(carts.Store[cart.Id].Items).Quantity);
        Assert.Equal(Stamped, carts.Store[cart.Id].UpdatedAt);
    }

    [Fact]
    public async Task GetCart_WidgetDeletedFromTheCatalogueWhileInTheCart_DropsTheLineAndRepricesTheRest()
    {
        // Arrange — a basket holding two widgets, one of which is then hard-deleted. A widget with
        // no order history is deleted outright rather than archived, so its id survives only here.
        var widgets = new InMemoryWidgetRepository();
        var keeper = Sellable("WW-001", "Standard Widget Block Cobalt", 9.99m);
        var deleted = Sellable("WW-002", "Reinforced Widget Frame", 50.00m);
        widgets.Store[keeper.Id] = keeper;
        widgets.Store[deleted.Id] = deleted;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, keeper.Id, 2, Stamped, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, deleted.Id, 3, Stamped, CancellationToken.None);
        await widgets.DeleteAsync(deleted.Id, CancellationToken.None);

        var handler = new GetCartHandler(carts, widgets);

        // Act
        var result = await handler.Handle(new GetCartQuery(cart.Id, null), CancellationToken.None);

        // Assert — the basket still opens, and the vanished widget contributes neither a line, a
        // unit, nor a penny of its 150.00 to the subtotal.
        Assert.True(result.IsSuccess);
        Assert.Equal(keeper.Id, Assert.Single(result.Value!.Items).WidgetId);
        Assert.Equal(2, result.Value!.ItemCount);
        Assert.Equal(19.98m, result.Value!.Subtotal);
    }

    [Fact]
    public async Task Merge_GuestLinesForHiddenOrDeletedWidgets_DropsThemAndStillMergesTheRest()
    {
        // Arrange — a guest basket assembled before two of its three widgets left the storefront:
        // one hidden by a manager, one deleted outright.
        var widgets = new InMemoryWidgetRepository();
        var sellable = Sellable("WW-001", "Standard Widget Block Cobalt", 9.99m);
        var hidden = Sellable("WW-002", "Reinforced Widget Frame", 50.00m);
        var deleted = Sellable("WW-003", "Compact Widget Hub", 30.00m);
        hidden.IsActive = false;
        widgets.Store[sellable.Id] = sellable;
        widgets.Store[hidden.Id] = hidden;
        widgets.Store[deleted.Id] = deleted;

        var carts = new InMemoryCartRepository();
        var guest = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(guest.Id, sellable.Id, 2, Stamped, CancellationToken.None);
        await carts.UpsertItemAsync(guest.Id, hidden.Id, 1, Stamped, CancellationToken.None);
        await carts.UpsertItemAsync(guest.Id, deleted.Id, 4, Stamped, CancellationToken.None);
        await widgets.DeleteAsync(deleted.Id, CancellationToken.None);

        var user = Guid.NewGuid();
        var handler = new MergeCartHandler(carts, widgets, new FakeTimeProvider(Now));

        // Act
        var result = await handler.Handle(new MergeCartCommand(user, guest.Id), CancellationToken.None);

        // Assert — signing in carries over only what is still purchasable, and the guest cart is gone.
        Assert.True(result.IsSuccess);
        Assert.Equal(user, result.Value!.UserId);
        Assert.Equal(sellable.Id, Assert.Single(result.Value!.Items).WidgetId);
        Assert.Equal(2, result.Value!.ItemCount);
        Assert.Equal(19.98m, result.Value!.Subtotal);
        Assert.DoesNotContain(guest.Id, carts.Store.Keys);
    }

    [Fact]
    public async Task Merge_GuestLineForAWidgetNowFullyReserved_DropsItRatherThanMergingAZeroQuantityLine()
    {
        // Arrange — a widget still on sale but with every unit already spoken for, so the quantity
        // the merge is allowed to carry over clamps to nothing.
        var widgets = new InMemoryWidgetRepository();
        var sellable = Sellable("WW-001", "Standard Widget Block Cobalt", 9.99m);
        var soldOut = Sellable("WW-002", "Reinforced Widget Frame", 50.00m);
        soldOut.QuantityReserved = soldOut.QuantityOnHand;
        widgets.Store[sellable.Id] = sellable;
        widgets.Store[soldOut.Id] = soldOut;

        var carts = new InMemoryCartRepository();
        var guest = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(guest.Id, sellable.Id, 2, Stamped, CancellationToken.None);
        await carts.UpsertItemAsync(guest.Id, soldOut.Id, 3, Stamped, CancellationToken.None);

        var user = Guid.NewGuid();
        var handler = new MergeCartHandler(carts, widgets, new FakeTimeProvider(Now));

        // Act
        var result = await handler.Handle(new MergeCartCommand(user, guest.Id), CancellationToken.None);

        // Assert — a zero-quantity line would show the shopper an item they cannot buy and cannot
        // remove a unit from, so it is never written at all.
        Assert.True(result.IsSuccess);
        Assert.Equal(sellable.Id, Assert.Single(result.Value!.Items).WidgetId);
        Assert.Equal(2, result.Value!.ItemCount);
        Assert.Equal(19.98m, result.Value!.Subtotal);
        Assert.Equal(0, soldOut.QuantityAvailable);
    }
}
