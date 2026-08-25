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
using WidgetWorks.WebApi.RateLimiting;
using WidgetWorks.WebApi.TwoFactor;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddWidgetWorksRateLimiting(builder.Configuration);

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // interactive API UI at /scalar/v1
}

app.UseCors(SpaCorsPolicy);

// Ahead of authentication on purpose: a throttled request is rejected before the app spends
// work validating credentials, which is what keeps a guessing flood cheap to absorb.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// 200 only when the database is actually usable. A 503 naming the failure is what turns a silent
// restart loop into a one-line diagnosis.
app.MapGet("/health", (TimeProvider clock) => migration.Successful
    ? Results.Ok(new { status = "ok", utcNow = clock.GetUtcNow() })
    : Results.Json(
        new { status = "unhealthy", reason = "database migration failed", detail = migration.Error, utcNow = clock.GetUtcNow() },
        statusCode: StatusCodes.Status503ServiceUnavailable));
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
