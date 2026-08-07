using System.Security.Claims;
using WidgetWorks.Application.Security.SecureAccount;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.WebApi.Security;

public static class SecurityEndpoints
{
    public static void MapSecurityEndpoints(this IEndpointRouteBuilder routes)
    {
        // Self-service: the signed-in user secures their own account (compromise response).
        routes.MapPost("/auth/secure-account", async (ClaimsPrincipal principal, SecureAccountHandler handler, CancellationToken ct) =>
        {
            var sub = principal.FindFirst("sub")?.Value;
            if (!Guid.TryParse(sub, out var userId))
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new SecureAccountCommand(userId), ct);
            return result.IsSuccess ? Results.NoContent() : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization();

        // Admin-initiated: revoke all sessions for a (possibly compromised) user.
        routes.MapPost("/admin/users/{userId:guid}/revoke-sessions", async (Guid userId, SecureAccountHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new SecureAccountCommand(userId), ct);
            return result.IsSuccess ? Results.NoContent() : Results.NotFound(new { error = result.Error });
        }).RequireAuthorization(policy => policy.RequireRole(UserRoles.Administrator));
    }
}
