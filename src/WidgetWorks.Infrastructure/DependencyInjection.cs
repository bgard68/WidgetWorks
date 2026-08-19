using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using WidgetWorks.Application;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Auth;
using WidgetWorks.Infrastructure.Email;
using WidgetWorks.Infrastructure.Payments;
using WidgetWorks.Infrastructure.Persistence;
using WidgetWorks.Infrastructure.Pricing;
using WidgetWorks.Infrastructure.Security;
using WidgetWorks.Infrastructure.Seeding;

namespace WidgetWorks.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers infrastructure services: data access, security, clock, seeder, audit, 2FA, catalog, cart, pricing, orders, payments, email, Google.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Dapper maps snake_case columns to PascalCase properties.
        DapperConfiguration.Apply();

        // Deterministic, testable time everywhere — never DateTime.Now.
        services.AddSingleton(TimeProvider.System);

        // Shared HttpClient for outbound calls (Stripe, Google JWKS).
        services.TryAddSingleton<HttpClient>();

        var connectionString = BuildConnectionString(configuration);
        services.AddSingleton<IDbConnectionFactory>(new NpgsqlConnectionFactory(connectionString));

        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));

        var accountSecurity = new AccountSecurityOptions();
        configuration.GetSection("AccountSecurity").Bind(accountSecurity);
        services.AddSingleton(accountSecurity);

        var appOptions = new AppOptions();
        configuration.GetSection("App").Bind(appOptions);
        services.AddSingleton(appOptions);

        var googleOptions = new GoogleOptions();
        configuration.GetSection("Google").Bind(googleOptions);
        services.AddSingleton(googleOptions);

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<ITwoFactorRepository, TwoFactorRepository>();
        services.AddScoped<IWidgetRepository, WidgetRepository>();
        services.AddScoped<ICartRepository, CartRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ITotpService, TotpService>();
        services.AddSingleton<IRecoveryCodes, RecoveryCodeService>();
        services.AddSingleton<ISecureTokenGenerator, SecureTokenGenerator>();
        services.AddSingleton<IGoogleTokenValidator, GoogleTokenValidator>();
        services.AddSingleton<IShippingCalculator, FlatRateShippingCalculator>();
        services.AddSingleton<ITaxRateProvider, StaticStateTaxRateProvider>();
        services.AddSingleton<ITaxCalculator, StateSalesTaxCalculator>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<DbSeeder>();

        // Payments: Mock by default; Stripe test-mode adapter selected by config (secret never committed).
        // Each provider registers its matching webhook parser so async settlement can be finalized.
        var paymentsProvider = configuration["Payments:Provider"] ?? "Mock";
        if (string.Equals(paymentsProvider, "Stripe", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<StripeOptions>(configuration.GetSection("Payments:Stripe"));
            services.AddScoped<IPaymentGateway, StripePaymentGateway>();
            services.AddScoped<IPaymentWebhookParser, StripePaymentWebhookParser>();
        }
        else
        {
            services.Configure<MockPaymentOptions>(configuration.GetSection("Payments:Mock"));
            services.AddScoped<IPaymentGateway, MockPaymentGateway>();
            services.AddScoped<IPaymentWebhookParser, MockPaymentWebhookParser>();
        }

        // Email: Dev sender (logs to stdout) by default; real SMTP selected by config (secret never committed).
        var emailOptions = new EmailOptions();
        configuration.GetSection("Email").Bind(emailOptions);
        services.AddSingleton(emailOptions);
        if (string.Equals(emailOptions.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, DevEmailSender>();
        }

        return services;
    }

    /// <summary>Builds the Postgres connection string from config, avoiding any committed secret literal.</summary>
    public static string BuildConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("WidgetWorks");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration["Postgres:Host"] ?? "localhost",
            Port = int.TryParse(configuration["Postgres:Port"], out var port) ? port : 5432,
            Database = configuration["Postgres:Database"] ?? "widgetworks",
            Username = configuration["Postgres:Username"] ?? "widgetworks",
            Password = configuration["Postgres:Password"] ?? string.Empty,
        };
        return builder.ConnectionString;
    }
}
