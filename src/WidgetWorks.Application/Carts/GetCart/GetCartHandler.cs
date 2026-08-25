using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Application.Carts;

namespace WidgetWorks.Application.Carts.GetCart;

public sealed record GetCartQuery(Guid CartId, Guid? RequestedBy);

public sealed class GetCartHandler(ICartRepository carts, IWidgetRepository widgets)
{
    public async Task<Result<CartView>> Handle(GetCartQuery query, CancellationToken ct)
    {
        var cart = await carts.GetAsync(query.CartId, ct);
        if (cart is null)
        {
            return Result<CartView>.Fail("Cart not found.");
        }

        if (!CartAccess.IsPermitted(cart, query.RequestedBy))
        {
            // Deliberately the same answer as a missing cart: telling an unauthorized caller that
            // the cart exists would confirm a guess.
            return Result<CartView>.Fail("Cart not found.");
        }

        return Result<CartView>.Success(await CartAssembler.BuildAsync(cart, widgets, ct));
    }
}
