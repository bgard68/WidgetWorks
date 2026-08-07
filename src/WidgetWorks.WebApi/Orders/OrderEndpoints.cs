using System.Security.Claims;
using WidgetWorks.Application.Orders.GetMine;
using WidgetWorks.Application.Orders.ListMine;
using WidgetWorks.Application.Orders.Lookup;

namespace WidgetWorks.WebApi.Orders;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        // Guest order tracking by order number + email (anonymous).
        routes.MapGet("/orders/lookup", async (string number, string email, GuestOrderLookupHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new GuestOrderLookupQuery(number, email), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        var mine = routes.MapGroup("/orders").RequireAuthorization();

        mine.MapGet("", async (ClaimsPrincipal principal, ListMyOrdersHandler handler, CancellationToken ct) =>
        {
            if (UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await handler.Handle(new ListMyOrdersQuery(userId), ct));
        });

        mine.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal principal, GetMyOrderHandler handler, CancellationToken ct) =>
        {
            if (UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new GetMyOrderQuery(userId, id), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        static Guid? UserId(ClaimsPrincipal principal)
            => Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;
    }
}
