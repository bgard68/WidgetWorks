using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WidgetWorks.Application;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Infrastructure;
using WidgetWorks.Infrastructure.Migrations;
using WidgetWorks.Infrastructure.Seeding;
using WidgetWorks.WebApi.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = jwtSection["SigningKey"] ?? string.Empty;

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
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
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

builder.Services.AddAuthorization();

var app = builder.Build();

// Apply migrations and seed demo accounts on startup.
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
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", (TimeProvider clock) => Results.Ok(new { status = "ok", utcNow = clock.GetUtcNow() }));
app.MapAuthEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
