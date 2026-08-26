using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using WidgetWorks.Application;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure;
using WidgetWorks.Infrastructure.Migrations;
using WidgetWorks.Infrastructure.Security;
using WidgetWorks.Infrastructure.Seeding;
using WidgetWorks.WebApi.Auth;
using WidgetWorks.WebApi.Authorization;
using WidgetWorks.WebApi.Carts;
using WidgetWorks.WebApi.Catalog;
using WidgetWorks.WebApi.Checkout;
using WidgetWorks.WebApi.Orders;
using WidgetWorks.WebApi.Payments;
using WidgetWorks.WebApi.Security;
using WidgetWorks.Application.Checkout.ReleaseStale;
using WidgetWorks.WebApi.Diagnostics;
using WidgetWorks.WebApi.Hosting;
using WidgetWorks.WebApi.RateLimiting;
using WidgetWorks.WebApi.TwoFactor;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddWidgetWorksRateLimiting(builder.Configuration);
builder.Services.AddSingleton<ProxyConfigurationCheck>();

// Stock held by an order whose payment never settles is returned to sale on a timer. Options are
// bound here so the sweep can be retuned, or turned off for a host that should not run background
// work, without a code change.
var reservationOptions = new ReservationOptions();
builder.Configuration.GetSection("Reservations").Bind(reservationOptions);
builder.Services.AddSingleton(reservationOptions);
builder.Services.AddHostedService<ReservationSweeper>();

// CORS for the browser SPA (origins from config; sensible localhost defaults for dev).
const string SpaCorsPolicy = "spa";
var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "http://localhost:3000,http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options =>
{
    options.AddPolicy(SpaCorsPolicy, policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var jwtOptions = new JwtOptions();
builder.Configuration.GetSection("Jwt").Bind(jwtOptions);
var keyRing = new JwtKeyRing(jwtOptions);
builder.Services.AddSingleton(keyRing);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKeyResolver = (_, _, kid, _) => keyRing.ResolveKeys(kid),

            // The default tolerance is five minutes, which silently turns a fifteen-minute access
            // token into a twenty-minute one. Thirty seconds is enough for ordinary clock drift
            // between hosts and makes the configured lifetime mean what it says.
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                var sub = context.Principal?.FindFirst("sub")?.Value;
                var stamp = context.Principal?.FindFirst("stamp")?.Value;
                if (!Guid.TryParse(sub, out var userId) ||
                    await users.GetSecurityStampAsync(userId, context.HttpContext.RequestAborted) is not { } current ||
                    current.ToString() != stamp)
                {
                    context.Fail("Security stamp mismatch.");
                }
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.ManageCatalog, policy => policy.RequireRole(UserRoles.Manager, UserRoles.Administrator));
    options.AddPolicy(Policies.ManageUsers, policy => policy.RequireRole(UserRoles.Administrator));
    options.AddPolicy(Policies.DeleteCatalog, policy => policy.RequireRole(UserRoles.Administrator));
});

var app = builder.Build();

// Apply migrations and seed demo accounts + demo widgets on startup.
//
// Deliberately does NOT throw when the database is unreachable. Throwing here exits the process,
// the host restarts it, and it fails again — a restart loop that silently consumes a free tier's
// daily CPU allowance and reports nothing useful. Instead the app starts, /health reports
// unhealthy and says why, and the operator fixes the setting and restarts once. The retry inside
// TryRun covers the common benign case: a serverless database still waking from idle.
var connectionString = WidgetWorks.Infrastructure.DependencyInjection.BuildConnectionString(app.Configuration);
var migration = MigrationRunner.TryRun(
    connectionString,
    log: message => app.Logger.LogWarning("{Message}", message));

if (migration.Successful)
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
    var seed = new SeedOptions();
    app.Configuration.GetSection("Seed").Bind(seed);
    await seeder.SeedAsync(seed, CancellationToken.None);
}
else
{
    // Loud, once, with the reason — the thing a restart loop never gives you.
    app.Logger.LogCritical(
        "Database unavailable after {Attempts} attempts: {Error}. The API is running but every " +
        "data request will fail; /health reports unhealthy. Check ConnectionStrings__WidgetWorks.",
        migration.Attempts,
        migration.Error);
}

// Once at startup: the throttling budgets are per-instance, so a tier that can run several of them
// silently multiplies every limit. Checked here rather than assumed in a comment, because the
// failure has no symptom — the limits just stop meaning what appsettings.json says.
ScaleOutCheck.Inspect(
    Environment.GetEnvironmentVariable("WEBSITE_SKU"),
    warning => app.Logger.LogWarning("{Message}", warning));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // interactive API UI at /scalar/v1
}

// First, so it wraps every other piece of middleware: anything that throws below this point
// becomes a correlated 500 rather than an empty one.
app.UseWidgetWorksExceptionHandler();

app.UseCors(SpaCorsPolicy);

// Watches real traffic for the proxy misconfiguration that would otherwise turn per-caller
// throttling into a global cap without anything saying so.
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<ProxyConfigurationCheck>().Inspect(context);
    await next(context);
});

// Ahead of authentication on purpose: a throttled request is rejected before the app spends
// work validating credentials, which is what keeps a guessing flood cheap to absorb.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness at /health (cheap, no database — the keep-warm schedule pings it) and readiness at
// /health/ready (queries the database, for platform probes and alerting).
app.MapHealthEndpoints(migration.Successful, migration.Error);
app.MapAuthEndpoints();
app.MapSecurityEndpoints();
app.MapTwoFactorEndpoints();
app.MapCatalogEndpoints();
app.MapCartEndpoints();
app.MapCheckoutEndpoints();
app.MapOrderEndpoints();
app.MapPaymentWebhookEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
