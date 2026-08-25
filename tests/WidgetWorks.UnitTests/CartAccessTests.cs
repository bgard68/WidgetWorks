using WidgetWorks.Application.Carts;
using WidgetWorks.Application.Carts.GetCart;
using WidgetWorks.Domain.Carts;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The cart authorization rule and its effect at the boundary. A cart id is a capability, so these
/// tests pin the line between "anyone holding the id" (guest) and "only the owner" (claimed).
/// </summary>
public class CartAccessTests
{
    private static Cart CartOwnedBy(Guid? owner) => new() { Id = Guid.NewGuid(), UserId = owner };

    [Fact]
    public void A_guest_cart_is_reachable_by_anyone_holding_its_id()
    {
        var cart = CartOwnedBy(null);

        // This is what lets a visitor shop before signing in.
        Assert.True(CartAccess.IsPermitted(cart, null));
        Assert.True(CartAccess.IsPermitted(cart, Guid.NewGuid()));
    }

    [Fact]
    public void A_claimed_cart_is_reachable_only_by_its_owner()
    {
        var owner = Guid.NewGuid();
        var cart = CartOwnedBy(owner);

        Assert.True(CartAccess.IsPermitted(cart, owner));
        Assert.False(CartAccess.IsPermitted(cart, Guid.NewGuid()));
        Assert.False(CartAccess.IsPermitted(cart, null));
    }

    [Fact]
    public async Task Reading_someone_elses_cart_is_refused_and_says_only_not_found()
    {
        var carts = new InMemoryCartRepository();
        var widgets = new InMemoryWidgetRepository();
        var owner = Guid.NewGuid();
        var cart = await carts.CreateAsync(owner, CancellationToken.None);

        var result = await new GetCartHandler(carts, widgets)
            .Handle(new GetCartQuery(cart.Id, Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        // The wording matters as much as the refusal: a distinct "forbidden" would confirm that a
        // cart with this id exists, which is exactly what a guesser wants to learn.
        Assert.Equal("Cart not found.", result.Error);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_reach_a_claimed_cart()
    {
        var carts = new InMemoryCartRepository();
        var widgets = new InMemoryWidgetRepository();
        var cart = await carts.CreateAsync(Guid.NewGuid(), CancellationToken.None);

        var result = await new GetCartHandler(carts, widgets)
            .Handle(new GetCartQuery(cart.Id, null), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task The_owner_can_still_read_their_own_cart()
    {
        var carts = new InMemoryCartRepository();
        var widgets = new InMemoryWidgetRepository();
        var owner = Guid.NewGuid();
        var cart = await carts.CreateAsync(owner, CancellationToken.None);

        var result = await new GetCartHandler(carts, widgets)
            .Handle(new GetCartQuery(cart.Id, owner), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task A_guest_cart_still_works_for_an_anonymous_shopper()
    {
        var carts = new InMemoryCartRepository();
        var widgets = new InMemoryWidgetRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);

        var result = await new GetCartHandler(carts, widgets)
            .Handle(new GetCartQuery(cart.Id, null), CancellationToken.None);

        // The whole guest checkout flow depends on this staying true.
        Assert.True(result.IsSuccess);
    }
}
