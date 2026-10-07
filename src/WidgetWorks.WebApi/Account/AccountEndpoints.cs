using System.Security.Claims;
using WidgetWorks.Application.Account.ChangePassword;
using WidgetWorks.Application.Account.Profile;
using WidgetWorks.Application.TwoFactor.Disable;
using WidgetWorks.WebApi.Authorization;
using WidgetWorks.WebApi.RateLimiting;

namespace WidgetWorks.WebApi.Account;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var account = routes.MapGroup("/account").RequireAuthorization();

        account.MapGet("/profile", async (ClaimsPrincipal principal, GetProfileHandler handler, CancellationToken ct) =>
        {
            if (UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new GetProfileQuery(userId), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        account.MapPut("/profile", async (UpdateProfileRequest body, ClaimsPrincipal principal, UpdateProfileHandler handler, CancellationToken ct) =>
        {
            if (UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new UpdateProfileCommand(userId, body.DisplayName), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        // Throttled with the auth budget rather than left open: it takes a password, so it is a
        // guessing surface even though the caller is already signed in.
        account.MapPost("/password", async (ChangePasswordRequest body, ClaimsPrincipal principal, ChangePasswordHandler handler, CancellationToken ct) =>
        {
            if (UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var result = await handler.Handle(new ChangePasswordCommand(userId, body.CurrentPassword, body.NewPassword), ct);

            // The new token pair comes back because the change signed every session out, this one
            // included. The client swaps them and stays where it is.
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        }).RequireRateLimiting(RateLimitPolicies.Auth);

        // Administrator only, never Manager. Clearing someone's second factor is an account-takeover
        // primitive — it is the one thing standing between a password and an account — so it sits with
        // user administration rather than with order management.
        //
        // It exists because the alternative is worse: a customer who loses both their authenticator and
        // their recovery codes has no self-service way back, and without this there is no way back at
        // all. Company A answers the same problem with staffed identity verification; this is the
        // small-merchant version of that, and it assumes the caller has verified the person out of band.
        routes.MapPost("/admin/users/{userId:guid}/reset-2fa", async (
            Guid userId,
            ClaimsPrincipal principal,
            DisableTwoFactorHandler handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new DisableTwoFactorCommand(userId, UserId(principal)), ct);
            return result.IsSuccess ? Results.Ok(new { reset = true }) : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization(Policies.ManageUsers);

        static Guid? UserId(ClaimsPrincipal principal)
            => Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;
    }

    /// <summary>Null or blank clears the name.</summary>
    public sealed record UpdateProfileRequest(string? DisplayName);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
}
