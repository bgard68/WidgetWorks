using System.Security.Claims;
using WidgetWorks.Application.Orders.Admin;
using WidgetWorks.Application.Orders.GetMine;
using WidgetWorks.Application.Orders.ListMine;
using WidgetWorks.Application.Orders.ListRecent;
using WidgetWorks.Application.Orders.Lookup;
using WidgetWorks.Application.Orders.UpdateStatus;
using WidgetWorks.WebApi.Authorization;

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

        // Admin/manager order management (ManageCatalog covers widgets, inventory, and orders).
        var admin = routes.MapGroup("/admin/orders").RequireAuthorization(Policies.ManageCatalog);

        // Staff order list. Summary rows only — open one to load its items.
        admin.MapGet("/", async (int? limit, ListRecentOrdersHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new ListRecentOrdersQuery(limit ?? 50), ct);
            return Results.Ok(result);
        });

        admin.MapGet("/{id:guid}", async (Guid id, GetOrderByIdHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetOrderByIdQuery(id), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        admin.MapPost("/{id:guid}/status", async (Guid id, UpdateStatusRequest body, UpdateOrderStatusHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new UpdateOrderStatusCommand(id, body.Status, body.TrackingNumber), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        static Guid? UserId(ClaimsPrincipal principal)
            => Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;
    }

    public sealed record UpdateStatusRequest(string Status, string? TrackingNumber);
}
