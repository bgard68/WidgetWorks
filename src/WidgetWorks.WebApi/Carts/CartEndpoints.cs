using System.Security.Claims;
using WidgetWorks.Application.Carts.AddItem;
using WidgetWorks.Application.Carts.GetCart;
using WidgetWorks.Application.Carts.Merge;
using WidgetWorks.Application.Carts.RemoveItem;
using WidgetWorks.Application.Carts.UpdateItem;

namespace WidgetWorks.WebApi.Carts;

public static class CartEndpoints
{
    public static void MapCartEndpoints(this IEndpointRouteBuilder routes)
    {
        var cart = routes.MapGroup("/cart");

        cart.MapGet("/{cartId:guid}", async (Guid cartId, ClaimsPrincipal principal, GetCartHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetCartQuery(cartId, UserId(principal)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        // Anonymous for guests; when a bearer token is present the cart is associated with the user.
        cart.MapPost("/items", async (AddItemRequest body, ClaimsPrincipal principal, AddCartItemHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new AddCartItemCommand(body.CartId, UserId(principal), body.WidgetId, body.Quantity), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        cart.MapPut("/{cartId:guid}/items/{widgetId:guid}", async (Guid cartId, Guid widgetId, UpdateItemRequest body, ClaimsPrincipal principal, UpdateCartItemHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new UpdateCartItemCommand(cartId, widgetId, body.Quantity, UserId(principal)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        cart.MapDelete("/{cartId:guid}/items/{widgetId:guid}", async (Guid cartId, Guid widgetId, ClaimsPrincipal principal, RemoveCartItemHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new RemoveCartItemCommand(cartId, widgetId, UserId(principal)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        // Merge a guest cart into the signed-in user's cart (called right after login).
        cart.MapPost("/merge", async (MergeRequest body, ClaimsPrincipal principal, MergeCartHandler handler, CancellationToken ct) =>
        {
            if (UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new MergeCartCommand(userId, body.GuestCartId), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization();

        static Guid? UserId(ClaimsPrincipal principal)
            => Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;
    }

    public sealed record AddItemRequest(Guid? CartId, Guid WidgetId, int Quantity);

    public sealed record UpdateItemRequest(int Quantity);

    public sealed record MergeRequest(Guid GuestCartId);
}
