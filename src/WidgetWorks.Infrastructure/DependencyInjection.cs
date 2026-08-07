using Microsoft.Extensions.DependencyInjection;

namespace WidgetWorks.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers infrastructure services (data access, adapters, clock).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // Deterministic, testable time everywhere — never DateTime.Now.
        services.AddSingleton(TimeProvider.System);

        // Phase 1+: Dapper repositories, JWT service, payment/email adapters, DbUp migrations.
        return services;
    }
}
