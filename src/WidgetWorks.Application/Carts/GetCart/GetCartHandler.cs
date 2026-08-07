using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Carts.GetCart;

public sealed record GetCartQuery(Guid CartId);

public sealed class GetCartHandler(ICartRepository carts, IWidgetRepository widgets)
{
    public async Task<Result<CartView>> Handle(GetCartQuery query, CancellationToken ct)
    {
        var cart = await carts.GetAsync(query.CartId, ct);
        if (cart is null)
        {
            return Result<CartView>.Fail("Cart not found.");
        }

        return Result<CartView>.Success(await CartAssembler.BuildAsync(cart, widgets, ct));
    }
}
