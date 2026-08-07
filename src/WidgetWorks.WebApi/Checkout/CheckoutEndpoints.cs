using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.Quote;

namespace WidgetWorks.WebApi.Checkout;

public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/checkout");

        group.MapGet("/shipping-methods", (IShippingCalculator shipping) => Results.Ok(shipping.AvailableMethods));

        // Anonymous: guests and registered users can preview totals before placing an order.
        group.MapPost("/quote", async (QuoteRequest body, QuoteCartHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new QuoteCartCommand(body.CartId, body.StateCode, body.ShippingMethod), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });
    }

    public sealed record QuoteRequest(Guid CartId, string? StateCode, string? ShippingMethod);
}
