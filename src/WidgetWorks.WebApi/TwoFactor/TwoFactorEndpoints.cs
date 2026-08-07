using System.Security.Claims;
using WidgetWorks.Application.TwoFactor.Confirm;
using WidgetWorks.Application.TwoFactor.Disable;
using WidgetWorks.Application.TwoFactor.Enroll;

namespace WidgetWorks.WebApi.TwoFactor;

public sealed record ConfirmEnrollRequest(string Code);

public static class TwoFactorEndpoints
{
    public static void MapTwoFactorEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/2fa").RequireAuthorization();

        group.MapPost("/enroll", async (ClaimsPrincipal principal, EnrollHandler handler, CancellationToken ct) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new EnrollCommand(userId), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/enroll/confirm", async (ConfirmEnrollRequest body, ClaimsPrincipal principal, ConfirmEnrollHandler handler, CancellationToken ct) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new ConfirmEnrollCommand(userId, body.Code), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/disable", async (ClaimsPrincipal principal, DisableTwoFactorHandler handler, CancellationToken ct) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new DisableTwoFactorCommand(userId), ct);
            return result.IsSuccess ? Results.NoContent() : Results.BadRequest(new { error = result.Error });
        });
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst("sub")?.Value, out userId);
}
