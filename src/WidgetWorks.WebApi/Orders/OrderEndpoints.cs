using System.Security.Claims;
using WidgetWorks.Application.Orders.Admin;
using WidgetWorks.Application.Orders.GetMine;
using WidgetWorks.Application.Orders.ListMine;
using WidgetWorks.Application.Orders.ListRecent;

using WidgetWorks.Application.Orders.Lookup;
using WidgetWorks.Application.Orders.Refund;
using WidgetWorks.Application.Orders.UpdateStatus;
using WidgetWorks.WebApi.Authorization;
using WidgetWorks.WebApi.RateLimiting;

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
        }).RequireRateLimiting(RateLimitPolicies.Lookup);

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

        // Everything on the payment path that needs a person: charges the provider never confirmed, and
        // orders that look like the same purchase twice. Registered before the {id} route so the
        // literal segment is not swallowed by it.
        admin.MapGet("/payment-exceptions", async (ListPaymentExceptionsHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.Handle(ct)));

        admin.MapGet("/{id:guid}", async (Guid id, GetOrderByIdHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetOrderByIdQuery(id), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        admin.MapPost("/{id:guid}/status", async (Guid id, UpdateStatusRequest body, ClaimsPrincipal principal, UpdateOrderStatusHandler handler, CancellationToken ct) =>
        {
            // The actor travels with the command so the audit trail can answer "who moved this order".
            var result = await handler.Handle(new UpdateOrderStatusCommand(id, body.Status, body.TrackingNumber, UserId(principal)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        // Refunding moves money, so it is its own route rather than a value on the status endpoint —
        // which cannot issue a refund and must not be able to claim one happened.
        admin.MapPost("/{id:guid}/refund", async (Guid id, RefundRequest? body, ClaimsPrincipal principal, RefundOrderHandler handler, CancellationToken ct) =>
        {
            // No body, or no amount in it, means the whole remaining balance — the common case, and the
            // one a staff member clicking a button expects.
            var result = await handler.Handle(new RefundOrderCommand(id, UserId(principal), body?.Amount), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        static Guid? UserId(ClaimsPrincipal principal)
            => Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;
    }

    public sealed record UpdateStatusRequest(string Status, string? TrackingNumber);

    /// <summary>Omit the amount to refund everything still owed.</summary>
    public sealed record RefundRequest(decimal? Amount);
}
