using Microsoft.AspNetCore.Diagnostics;

namespace WidgetWorks.WebApi.Diagnostics;

public static class ExceptionHandling
{
    /// <summary>
    /// Turns an unhandled exception into a supportable answer.
    ///
    /// The previous behaviour was safe but opaque: no handler meant a bare 500 with an empty body,
    /// and while that leaks nothing — the developer exception page is Development-only — it left
    /// nothing connecting what the customer saw to what the logs recorded.
    ///
    /// The response now carries a correlation id and the same id is on the log line, so a report
    /// becomes a lookup. The body still says nothing about the failure itself: an exception message
    /// can name a host, a column, or a connection string, and this reaches anonymous callers.
    /// </summary>
    public static void UseWidgetWorksExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(builder => builder.Run(async context =>
        {
            var correlationId = CorrelationId.Resolve(context);
            var feature = context.Features.Get<IExceptionHandlerFeature>();

            // The path is sanitised because Request.Path.Value is the *decoded* path: a URL
            // containing %0A arrives here as a real newline, which would end this log entry and
            // begin one the caller wrote (CWE-117). The correlation id is already clean by
            // construction; the method comes from the server's own parser.
            app.Logger.LogError(
                feature?.Error,
                "Unhandled exception for {Method} {Path} (correlation {CorrelationId}).",
                LogSafe.Text(context.Request.Method, maxLength: 16),
                LogSafe.Text(context.Request.Path.Value),
                correlationId);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.Headers[CorrelationId.HeaderName] = correlationId;

            await context.Response.WriteAsJsonAsync(new
            {
                error = "Something went wrong on our side. Quote the reference below if you contact us.",
                correlationId,
            });
        }));

        // Echoed on every response, not only failures, so a caller can correlate a slow or wrong
        // answer as readily as a failed one — and so a support conversation can start before anyone
        // has looked at a log.
        app.Use(async (context, next) =>
        {
            var correlationId = CorrelationId.Resolve(context);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[CorrelationId.HeaderName] = correlationId;
                return Task.CompletedTask;
            });

            await next(context);
        });
    }
}
