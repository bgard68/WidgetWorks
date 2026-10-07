using System.Security.Claims;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Checkout.Quote;
using WidgetWorks.WebApi.RateLimiting;

namespace WidgetWorks.WebApi.Checkout;

public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder routes)
    {
        // Place an order. Anonymous: guests check out with email + address; a bearer token attaches the order to the user.
        //
        // Send an Idempotency-Key header to make the call retry-safe: the same key returns the first
        // order instead of placing a second one, and a key still being processed answers 409 rather
        // than charging twice. Omitting the header keeps the old behaviour, so existing clients are
        // unaffected — and unprotected.
        routes.MapPost("/checkout", async (CheckoutRequest body, ClaimsPrincipal principal, HttpRequest request, HttpResponse response, IdempotentCheckout handler, CancellationToken ct) =>
        {
            var command = new CheckoutCommand(
                body.CartId,
                UserId(principal),
                body.Email,
                new ShippingAddressInput(body.Name, body.Line1, body.Line2, body.City, body.State, body.PostalCode, body.Country),
                body.ShippingMethod,
                body.PaymentToken);

            var result = await handler.Handle(request.Headers["Idempotency-Key"].ToString(), command, ct);

            if (result.Replayed)
            {
                // Lets a client tell "my retry worked" from "I placed two orders" without reading
                // the body, and makes duplicate traffic visible in access logs.
                response.Headers["Idempotent-Replay"] = "true";
            }

            return result.Outcome switch
            {
                CheckoutOutcome.Placed => Results.Ok(result.Value),

                // 409 rather than 400: nothing is wrong with the request, it simply cannot be
                // answered yet, or was already answered for different content. The code travels with
                // it so a client can tell "wait and retry" from "this will never work" without
                // matching on the prose.
                CheckoutOutcome.Conflict => Results.Json(
                    new { error = result.Error, code = result.ConflictCode },
                    statusCode: StatusCodes.Status409Conflict),

                _ => Results.BadRequest(new { error = result.Error }),
            };
        }).RequireRateLimiting(RateLimitPolicies.Checkout);

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
