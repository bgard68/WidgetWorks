using Microsoft.Extensions.DependencyInjection;
using WidgetWorks.Application.Auth.Login;
using WidgetWorks.Application.Auth.Logout;
using WidgetWorks.Application.Auth.Refresh;
using WidgetWorks.Application.Auth.Register;

namespace WidgetWorks.Application;

public static class DependencyInjection
{
    /// <summary>Registers the application layer (use-case handlers). No MediatR — plain handlers.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<RegisterHandler>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshHandler>();
        services.AddScoped<LogoutHandler>();
        return services;
    }
}
