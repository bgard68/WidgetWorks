using System.Security.Claims;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Checkout.Quote;

namespace WidgetWorks.WebApi.Checkout;

public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder routes)
    {
        // Place an order. Anonymous: guests check out with email + address; a bearer token attaches the order to the user.
        routes.MapPost("/checkout", async (CheckoutRequest body, ClaimsPrincipal principal, CheckoutHandler handler, CancellationToken ct) =>
        {
            var command = new CheckoutCommand(
                body.CartId,
                UserId(principal),
                body.Email,
                new ShippingAddressInput(body.Name, body.Line1, body.Line2, body.City, body.State, body.PostalCode, body.Country),
                body.ShippingMethod,
                body.PaymentToken);
            var result = await handler.Handle(command, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        var group = routes.MapGroup("/checkout");

        group.MapGet("/shipping-methods", (IShippingCalculator shipping) => Results.Ok(shipping.AvailableMethods));

        group.MapGet("/tax-info", (ITaxRateProvider rates) => Results.Ok(new
        {
            effectiveOn = rates.Current.EffectiveOn,
            source = rates.Current.Source,
            stateCount = rates.Current.Rates.Count,
        }));

        group.MapPost("/quote", async (QuoteRequest body, QuoteCartHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new QuoteCartCommand(body.CartId, body.StateCode, body.ShippingMethod), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });

        static Guid? UserId(ClaimsPrincipal principal)
            => Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;
    }

    public sealed record QuoteRequest(Guid CartId, string? StateCode, string? ShippingMethod);

    public sealed record CheckoutRequest(
        Guid CartId,
        string Email,
        string Name,
        string Line1,
        string? Line2,
        string City,
        string State,
        string PostalCode,
        string? Country,
        string? ShippingMethod,
        string? PaymentToken);
}
