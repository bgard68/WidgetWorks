using Microsoft.Extensions.DependencyInjection;
using WidgetWorks.Application.Auth.Google;
using WidgetWorks.Application.Auth.Login;
using WidgetWorks.Application.Auth.Logout;
using WidgetWorks.Application.Auth.PasswordReset;
using WidgetWorks.Application.Auth.Refresh;
using WidgetWorks.Application.Auth.Register;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Application.Carts.AddItem;
using WidgetWorks.Application.Carts.GetCart;
using WidgetWorks.Application.Carts.Merge;
using WidgetWorks.Application.Carts.RemoveItem;
using WidgetWorks.Application.Carts.UpdateItem;
using WidgetWorks.Application.Catalog.Browse;
using WidgetWorks.Application.Catalog.Create;
using WidgetWorks.Application.Catalog.Delete;
using WidgetWorks.Application.Catalog.Detail;
using WidgetWorks.Application.Catalog.Inventory;
using WidgetWorks.Application.Catalog.Update;
using WidgetWorks.Application.Checkout.ConfirmPayment;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Checkout.ReleaseStale;
using WidgetWorks.Application.Checkout.Quote;
using WidgetWorks.Application.Orders.Admin;
using WidgetWorks.Application.Orders.GetMine;
using WidgetWorks.Application.Orders.ListMine;
using WidgetWorks.Application.Orders.ListRecent;
using WidgetWorks.Application.Orders.Lookup;
using WidgetWorks.Application.Orders.UpdateStatus;
using WidgetWorks.Application.Security.SecureAccount;
using WidgetWorks.Application.TwoFactor.Challenge;
using WidgetWorks.Application.TwoFactor.Confirm;
using WidgetWorks.Application.TwoFactor.Disable;
using WidgetWorks.Application.TwoFactor.Enroll;
using WidgetWorks.Application.TwoFactor.Recovery;

namespace WidgetWorks.Application;

public static class DependencyInjection
{
    /// <summary>Registers the application layer (use-case handlers). No MediatR — plain handlers.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<OrderPricer>();
        services.AddScoped<RegisterHandler>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<GoogleLoginHandler>();
        services.AddScoped<RefreshHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<RequestPasswordResetHandler>();
        services.AddScoped<ResetPasswordHandler>();
        services.AddScoped<SecureAccountHandler>();
        services.AddScoped<EnrollHandler>();
        services.AddScoped<ConfirmEnrollHandler>();
        services.AddScoped<DisableTwoFactorHandler>();
        services.AddScoped<TwoFactorLoginHandler>();
        services.AddScoped<RecoveryLoginHandler>();
        services.AddScoped<BrowseWidgetsHandler>();
        services.AddScoped<GetWidgetHandler>();
        services.AddScoped<CreateWidgetHandler>();
        services.AddScoped<UpdateWidgetHandler>();
        services.AddScoped<DeleteWidgetHandler>();
        services.AddScoped<AdjustInventoryHandler>();
        services.AddScoped<GetCartHandler>();
        services.AddScoped<AddCartItemHandler>();
        services.AddScoped<UpdateCartItemHandler>();
        services.AddScoped<RemoveCartItemHandler>();
        services.AddScoped<MergeCartHandler>();
        services.AddScoped<QuoteCartHandler>();
        services.AddScoped<CheckoutHandler>();
        services.AddScoped<ReleaseStaleReservationsHandler>();
        services.AddScoped<ConfirmPaymentHandler>();
        services.AddScoped<GuestOrderLookupHandler>();
        services.AddScoped<ListMyOrdersHandler>();

        services.AddScoped<ListRecentOrdersHandler>();
        services.AddScoped<GetMyOrderHandler>();
        services.AddScoped<GetOrderByIdHandler>();
        services.AddScoped<UpdateOrderStatusHandler>();
        return services;
    }
}
