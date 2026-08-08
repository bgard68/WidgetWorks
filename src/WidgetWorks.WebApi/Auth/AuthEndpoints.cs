using WidgetWorks.Application.Auth.Google;
using WidgetWorks.Application.Auth.Login;
using WidgetWorks.Application.Auth.Logout;
using WidgetWorks.Application.Auth.PasswordReset;
using WidgetWorks.Application.Auth.Refresh;
using WidgetWorks.Application.Auth.Register;
using WidgetWorks.Application.TwoFactor.Challenge;
using WidgetWorks.Application.TwoFactor.Recovery;

namespace WidgetWorks.WebApi.Auth;

public sealed record TwoFactorLoginRequest(string ChallengeToken, string Code);

public sealed record RecoveryLoginRequest(string ChallengeToken, string RecoveryCode);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record GoogleLoginRequest(string IdToken);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth");

        group.MapPost("/register", async (RegisterCommand command, RegisterHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(command, ct);
            return result.IsSuccess
                ? Results.Ok()
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/login", async (LoginCommand command, LoginHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(command, ct);
            if (result.IsFailure)
            {
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var login = result.Value!;
            return login.RequiresTwoFactor
                ? Results.Ok(new { twoFactorRequired = true, challengeToken = login.ChallengeToken })
                : Results.Ok(login.Tokens);
        });

        group.MapPost("/google", async (GoogleLoginRequest body, GoogleLoginHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new GoogleLoginCommand(body.IdToken), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
        });

        group.MapPost("/2fa", async (TwoFactorLoginRequest body, TwoFactorLoginHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new TwoFactorLoginCommand(body.ChallengeToken, body.Code), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
        });

        group.MapPost("/2fa/recovery", async (RecoveryLoginRequest body, RecoveryLoginHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new RecoveryLoginCommand(body.ChallengeToken, body.RecoveryCode), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
        });

        // Always 200 regardless of whether the email maps to an account (no enumeration).
        group.MapPost("/forgot-password", async (ForgotPasswordRequest body, RequestPasswordResetHandler handler, CancellationToken ct) =>
        {
            await handler.Handle(new RequestPasswordResetCommand(body.Email), ct);
            return Results.Ok(new { message = "If that email has an account, a reset link is on its way." });
        });

        group.MapPost("/reset-password", async (ResetPasswordRequest body, ResetPasswordHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new ResetPasswordCommand(body.Token, body.NewPassword), ct);
            return result.IsSuccess
                ? Results.Ok()
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/refresh", async (RefreshCommand command, RefreshHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(command, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
        });

        group.MapPost("/logout", async (LogoutCommand command, LogoutHandler handler, CancellationToken ct) =>
        {
            await handler.Handle(command, ct);
            return Results.NoContent();
        });
    }
}
