using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.ConfirmPayment;

namespace WidgetWorks.WebApi.Payments;

public static class PaymentWebhookEndpoints
{
    public static void MapPaymentWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        // Provider webhook -> normalized event -> order settlement. Public, but each provider's parser
        // verifies the payload's signature. Unknown provider -> 404; unverifiable/malformed -> 400;
        // otherwise 200 (acknowledged), with the resulting order status (or "ignored").
        routes.MapPost("/webhooks/payments/{provider}", async (
            string provider,
            HttpRequest request,
            IEnumerable<IPaymentWebhookParser> parsers,
            ConfirmPaymentHandler handler,
            CancellationToken ct) =>
        {
            var parser = parsers.FirstOrDefault(p => string.Equals(p.Provider, provider, StringComparison.OrdinalIgnoreCase));
            if (parser is null)
            {
                return Results.NotFound(new { error = $"No webhook handler for provider '{provider}'." });
            }

            string payload;
            using (var reader = new StreamReader(request.Body))
            {
                payload = await reader.ReadToEndAsync(ct);
            }

            var signature = FirstHeader(request, "Stripe-Signature") ?? FirstHeader(request, "X-Webhook-Signature");

            if (!parser.TryParse(payload, signature, out var evt, out var error) || evt is null)
            {
                return Results.BadRequest(new { error = error ?? "Invalid webhook." });
            }

            var result = await handler.Handle(new ConfirmPaymentCommand(evt.Provider, evt.Type, evt.Reference), ct);
            return Results.Ok(new { status = result.IsSuccess ? result.Value : "ignored" });
        });

        static string? FirstHeader(HttpRequest request, string name)
            => request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
    }
}
