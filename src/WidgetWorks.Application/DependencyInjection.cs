using Microsoft.Extensions.DependencyInjection;

namespace WidgetWorks.Application;

public static class DependencyInjection
{
    /// <summary>Registers the application layer. Feature handlers are added here as they are built (no MediatR).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Phase 0: no handlers yet.
        return services;
    }
}
