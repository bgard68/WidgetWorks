using WidgetWorks.Application.Auth.Login;
using WidgetWorks.Application.Auth.Logout;
using WidgetWorks.Application.Auth.Refresh;
using WidgetWorks.Application.Auth.Register;

namespace WidgetWorks.WebApi.Auth;

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
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
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
