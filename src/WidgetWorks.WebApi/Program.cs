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
using WidgetWorks.WebApi.Security;
using WidgetWorks.WebApi.TwoFactor;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

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
});

var app = builder.Build();

// Apply migrations and seed demo accounts + demo widgets on startup.
var connectionString = WidgetWorks.Infrastructure.DependencyInjection.BuildConnectionString(app.Configuration);
MigrationRunner.Run(connectionString);

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
    var seed = new SeedOptions();
    app.Configuration.GetSection("Seed").Bind(seed);
    await seeder.SeedAsync(seed, CancellationToken.None);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // interactive API UI at /scalar/v1
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", (TimeProvider clock) => Results.Ok(new { status = "ok", utcNow = clock.GetUtcNow() }));
app.MapAuthEndpoints();
app.MapSecurityEndpoints();
app.MapTwoFactorEndpoints();
app.MapCatalogEndpoints();
app.MapCartEndpoints();
app.MapCheckoutEndpoints();
app.MapOrderEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
