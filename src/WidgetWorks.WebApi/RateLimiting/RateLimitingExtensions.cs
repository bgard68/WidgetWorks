using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WidgetWorks.WebApi.RateLimiting;

/// <summary>Named throttling policies, referenced by endpoints the way authorization policies are.</summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in, registration, password reset — the credential-guessing surface.</summary>
    public const string Auth = "auth";

    /// <summary>Order placement.</summary>
    public const string Checkout = "checkout";

    /// <summary>Guest order lookup.</summary>
    public const string Lookup = "lookup";
}

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers the throttling policies.
    ///
    /// Only the endpoints that are both anonymous and abusable carry a policy. There is deliberately
    /// no global limiter: a catalogue page issues several requests in a burst, so a global cap would
    /// throttle ordinary browsing while doing nothing an endpoint policy does not already do.
    /// </summary>
    public static IServiceCollection AddWidgetWorksRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new RateLimitOptions();
        configuration.GetSection("RateLimiting").Bind(options);
        services.AddSingleton(options);

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(RateLimitPolicies.Auth, ctx => Partition(ctx, options, options.Auth));
            limiter.AddPolicy(RateLimitPolicies.Checkout, ctx => Partition(ctx, options, options.Checkout));
            limiter.AddPolicy(RateLimitPolicies.Lookup, ctx => Partition(ctx, options, options.Lookup));

            limiter.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                // Tell the caller when to come back. A well-behaved client backs off instead of
                // retrying into the wall, and an honest one that hit the limit by accident recovers
                // without a support ticket.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests. Please wait a moment and try again." }, ct);
            };
        });

        return services;
    }

    /// <summary>
    /// One fixed window per caller. Fixed rather than sliding because the budgets here are small and
    /// the extra per-partition state a sliding window keeps is not worth paying for at this size.
    /// </summary>
    private static RateLimitPartition<string> Partition(HttpContext context, RateLimitOptions options, RateLimitBudget budget)
    {
        var (permits, window) = budget.Resolve();
        return RateLimitPartition.GetFixedWindowLimiter(
            ClientAddress.Resolve(context, options.TrustForwardedFor),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });
    }
}
