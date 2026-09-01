using System.Data;
using Dapper;
using WidgetWorks.Infrastructure.Persistence;

namespace WidgetWorks.WebApi.Diagnostics;

/// <summary>
/// Two probes answering two different questions, because conflating them is how a monitoring signal
/// ends up unable to report bad news.
/// </summary>
public static class HealthEndpoints
{
    /// <param name="migrationSucceeded">Outcome of the startup migration — a fact about this boot.</param>
    /// <param name="migrationError">Why it failed, when it did.</param>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder routes, bool migrationSucceeded, string? migrationError)
    {
        // Liveness. Answers "did this process start correctly", which is a fact settled at boot and
        // needs no database. Deliberately unchanged in contract: the provisioning script watches it
        // on first boot and stops the app when it reports unhealthy, and the keep-warm schedule
        // pings it every few minutes to hold a free-tier instance loaded.
        //
        // That ping is why this must not touch the database. Waking a serverless database on every
        // warm-up would hold a metered resource awake around the clock — roughly 180 CU-hrs against
        // a 100 CU-hr monthly budget. Warm the app, let the database sleep.
        // GET *and* HEAD: the keep-warm monitor and most platform probes default to HEAD, and a
        // liveness endpoint that answers 405 to the probe watching it cannot report bad news.
        routes.MapMethods("/health", new[] { "GET", "HEAD" }, (TimeProvider clock) => migrationSucceeded
            ? Results.Ok(new { status = "ok", utcNow = clock.GetUtcNow() })
            : Results.Json(
                new { status = "unhealthy", reason = "database migration failed", detail = migrationError, utcNow = clock.GetUtcNow() },
                statusCode: StatusCodes.Status503ServiceUnavailable));

        // Readiness. Answers "can this instance serve a request right now", which liveness cannot:
        // a process that started perfectly is still useless once its database goes away, and the
        // startup answer never changes to say so.
        //
        // Point platform probes and alerting here, not at /health — and keep scheduled warm-up
        // pings off it, or the cost the shallow probe exists to avoid comes straight back.
        routes.MapGet("/health/ready", async (
            IDbConnectionFactory connections,
            TimeProvider clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var startedAt = clock.GetUtcNow();
            try
            {
                using var db = await connections.OpenAsync(ct);
                await db.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: ct));

                return Results.Ok(new
                {
                    status = "ready",
                    database = "ok",
                    migrationSucceeded,
                    checkedAt = startedAt,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller gave up or the host is shutting down. Not a verdict about the database,
                // so it is reported as unavailable without being logged as a fault.
                return Results.Json(
                    new { status = "unavailable", reason = "the readiness check was cancelled", checkedAt = startedAt },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                // Logged rather than swallowed: the probe's 503 tells the platform to stop sending
                // traffic here, and this line is the only place the reason survives.
                loggerFactory
                    .CreateLogger(typeof(HealthEndpoints))
                    .LogError(ex, "Readiness check failed: the database did not answer.");

                return Results.Json(
                    new
                    {
                        status = "not ready",
                        database = "unreachable",
                        // The exception type, never its message: a connection failure can carry a
                        // host name or a user, and this endpoint is unauthenticated.
                        reason = ex.GetType().Name,
                        checkedAt = startedAt,
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
