using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Payments;

public sealed class StripeOptions
{
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Webhook signing secret (whsec_...). Required only if the Stripe webhook is used.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public string ApiBase { get; set; } = "https://api.stripe.com";

    /// <summary>
    /// Backoff before each retry of a charge whose outcome was not learned — a timeout, a 5xx, a
    /// 429. Two retries over about a second: long enough to ride out a blip, short enough that a
    /// shopper is still watching the spinner rather than reloading the page and trying again.
    ///
    /// Retrying a charge is only safe because every attempt carries the same Idempotency-Key, so
    /// Stripe returns the original PaymentIntent instead of creating a second one. Without the key
    /// this list would have to be empty.
    /// </summary>
    public int[] RetryDelaysMs { get; set; } = [200, 800];
}

/// <summary>
/// Stripe test-mode adapter. Creates and confirms a PaymentIntent via Stripe's REST API using an
/// HttpClient (no SDK dependency). A card charge settles synchronously (status "succeeded"); a redirect
/// or BNPL method returns "requires_action"/"processing", which maps to Pending — the order parks in
/// AwaitingPayment until the Stripe webhook (payment_intent.succeeded/…payment_failed) settles it.
/// The secret key comes only from configuration / user-secrets and is never committed. Selected by
/// config (Payments:Provider = Stripe); the Mock gateway is the default.
/// </summary>
public sealed class StripePaymentGateway(
    HttpClient http,
    IOptions<StripeOptions> options,
    ILogger<StripePaymentGateway> logger) : IPaymentGateway
{
    public string Name => "Stripe";

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            return PaymentResult.Declined(Name, "Stripe is not configured.");
        }

        if (request.Amount <= 0)
        {
            return PaymentResult.Declined(Name, "Amount must be positive.");
        }

        var amountMinor = (long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero);
        var form = new Dictionary<string, string>
        {
            ["amount"] = amountMinor.ToString(CultureInfo.InvariantCulture),
            ["currency"] = string.IsNullOrWhiteSpace(request.Currency) ? "usd" : request.Currency,
            ["confirm"] = "true",
            ["payment_method"] = string.IsNullOrWhiteSpace(request.PaymentToken) ? "pm_card_visa" : request.PaymentToken!,
            ["description"] = $"WidgetWorks order {request.OrderNumber}",
            // Correlate the eventual webhook back to our order.
            ["metadata[order_number]"] = request.OrderNumber,
            ["automatic_payment_methods[enabled]"] = "true",
            ["automatic_payment_methods[allow_redirects]"] = "never",
        };

        // Our own idempotency ledger protects the order; this protects the money. They are different
        // layers and both are needed: the ledger stops a duplicate HTTP request to *us*, and cannot
        // see a retry of the single outbound call below. Stripe honours a key for 24 hours, so a
        // replayed create returns the original PaymentIntent rather than charging a second time.
        //
        // The order number is the natural key: `ux_orders_number` makes it unique, and checkout
        // charges exactly once per order, so it names this charge attempt and no other. A genuinely
        // new attempt (a shopper retrying with a different card) is a new order with a new number,
        // and so a new key — which is right, because it really is a different charge.
        var idempotencyKey = $"order-{request.OrderNumber}";

        var attempt = await SendWithRetryAsync(settings, form, idempotencyKey, ct);

        if (attempt.Status is null)
        {
            // Never reached Stripe on any attempt — DNS, a dead socket, a client timeout. No request
            // arrived, so no charge exists, and declining does not abandon money.
            logger.LogWarning(
                "Stripe could not be reached for order {OrderNumber} after {Attempts} attempt(s).",
                request.OrderNumber,
                attempt.Attempts);
            return PaymentResult.Declined(Name, "The payment provider could not be reached.");
        }

        if (!attempt.Succeeded)
        {
            var code = (int)attempt.Status.Value;

            // A 4xx is Stripe's answer; anything else is Stripe failing to give one. The distinction
            // matters because the second case may have taken the money anyway — see the note on
            // reconciliation in docs/handbook/05-payments.md.
            if (IsTransient(attempt.Status.Value))
            {
                logger.LogError(
                    "Stripe returned {Code} for order {OrderNumber} on all {Attempts} attempt(s); the " +
                    "charge outcome is unknown and will be reconciled.",
                    code,
                    request.OrderNumber,
                    attempt.Attempts);

                // Not Declined. Stripe may have taken the money and failed to say so, and declining
                // would release the stock and tell the customer their payment failed.
                return PaymentResult.Indeterminate(Name, $"Stripe gave no outcome after {attempt.Attempts} attempt(s) (last status {code}).");
            }

            return PaymentResult.Declined(Name, $"Stripe returned {code}.");
        }

        using var doc = JsonDocument.Parse(attempt.Body);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
        var reference = id ?? "unknown";

        switch (status)
        {
            case "succeeded":
                return PaymentResult.Ok(Name, reference);

            case "requires_action":
            case "requires_confirmation":
            case "processing":
                var clientSecret = root.TryGetProperty("client_secret", out var cs) ? cs.GetString() : null;
                return PaymentResult.Pending(Name, reference, clientSecret, ExtractRedirectUrl(root));

            default:
                return PaymentResult.Declined(Name, $"Payment not completed (status: {status}).");
        }
    }

    /// <summary>
    /// Asks Stripe what became of this order's charge, by searching PaymentIntents on the
    /// <c>order_number</c> metadata written at charge time.
    ///
    /// A search, never a replayed create. Replaying the create under the same key would return the
    /// original intent when one exists — but would *create* one when it does not, which is the one
    /// thing a probe must never do: the order being reconciled may well have never been charged.
    ///
    /// Stripe's search index lags writes by up to a minute, so "nothing found" is reported as
    /// indeterminate rather than as a decline. Treating an empty result as proof of absence would fail
    /// orders that had in fact been paid seconds earlier.
    /// </summary>
    public async Task<PaymentResult> ProbeAsync(string orderNumber, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            return PaymentResult.Indeterminate(Name, "Stripe is not configured.");
        }

        var query = Uri.EscapeDataString($"metadata['order_number']:'{orderNumber}'");
        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"{settings.ApiBase}/v1/payment_intents/search?limit=1&query={query}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Still unknown. The next sweep asks again; nothing is decided on a failed question.
            return PaymentResult.Indeterminate(Name, "Stripe could not be reached.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return PaymentResult.Indeterminate(Name, $"Stripe search returned {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                return PaymentResult.Indeterminate(Name, "No charge found for this order yet.");
            }

            var intent = data[0];
            var status = intent.TryGetProperty("status", out var s) ? s.GetString() : null;
            var reference = intent.TryGetProperty("id", out var i) ? i.GetString() : null;

            if (string.IsNullOrEmpty(reference))
            {
                return PaymentResult.Indeterminate(Name, "Stripe returned a charge with no id.");
            }

            return status switch
            {
                "succeeded" => PaymentResult.Ok(Name, reference),

                // A real, settled refusal: the customer's card said no and no money moved.
                "canceled" or "requires_payment_method" => PaymentResult.Declined(Name, "The payment was not completed."),

                // Still in progress — a redirect the shopper has not finished, or an async capture.
                // Pending keeps the reservation and lets the webhook or a later sweep settle it.
                "requires_action" or "requires_confirmation" or "processing" => PaymentResult.Pending(Name, reference),

                _ => PaymentResult.Indeterminate(Name, $"Unrecognised charge status '{status}'."),
            };
        }
    }

    /// <summary>
    /// Refunds a charge. Carries the caller's idempotency key so a retried refund returns the original
    /// one rather than giving the money back twice — the same discipline as the charge, and it matters
    /// more here because nothing downstream would notice a double refund.
    /// </summary>
    public async Task<PaymentResult> RefundAsync(string reference, decimal amount, string idempotencyKey, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            return PaymentResult.Declined(Name, "Stripe is not configured.");
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            // No charge id means there is nothing to refund, and guessing would be worse than failing.
            return PaymentResult.Declined(Name, "The order has no payment reference to refund.");
        }

        if (amount <= 0)
        {
            return PaymentResult.Declined(Name, "Refund amount must be positive.");
        }

        var form = new Dictionary<string, string>
        {
            ["payment_intent"] = reference,
            ["amount"] = ((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBase}/v1/refunds")
        {
            Content = new FormUrlEncodedContent(form),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Not retried here and not reported as a failure: the refund may have gone through, and
            // telling staff it did not would invite them to issue a second one.
            logger.LogError(ex, "Refund of {Reference} could not be confirmed.", reference);
            return PaymentResult.Indeterminate(Name, "The refund could not be confirmed. Check the provider before retrying.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // A 4xx is Stripe refusing; a 5xx leaves it unknown, and the two must not be conflated
                // any more than they are on the charge path.
                if (IsTransient(response.StatusCode))
                {
                    logger.LogError("Refund of {Reference} returned {Code}; outcome unknown.", reference, (int)response.StatusCode);
                    return PaymentResult.Indeterminate(Name, "The refund could not be confirmed. Check the provider before retrying.");
                }

                return PaymentResult.Declined(Name, $"Stripe returned {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;

            return status switch
            {
                "succeeded" or "pending" => PaymentResult.Ok(Name, id ?? "unknown"),
                "failed" or "canceled" => PaymentResult.Declined(Name, $"The refund was not accepted (status: {status})."),
                _ => PaymentResult.Indeterminate(Name, $"Unrecognised refund status '{status}'."),
            };
        }
    }

    /// <summary>What the last attempt came back with. A null status means no attempt ever got a reply.</summary>
    private sealed record SendOutcome(HttpStatusCode? Status, bool Succeeded, string Body, int Attempts);

    /// <summary>
    /// Posts the charge, retrying only outcomes that leave it unknown, and always under the same
    /// Idempotency-Key — which is the single reason retrying a charge is safe at all. Each attempt
    /// needs its own <see cref="HttpRequestMessage"/>; one cannot be sent twice.
    /// </summary>
    private async Task<SendOutcome> SendWithRetryAsync(
        StripeOptions settings,
        Dictionary<string, string> form,
        string idempotencyKey,
        CancellationToken ct)
    {
        var delays = settings.RetryDelaysMs ?? [];

        for (var attempt = 0; ; attempt++)
        {
            var last = attempt == delays.Length;

            using var message = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBase}/v1/payment_intents")
            {
                Content = new FormUrlEncodedContent(form),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);
            message.Headers.Add("Idempotency-Key", idempotencyKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(message, ct);
            }
            catch (HttpRequestException) when (!last)
            {
                // No reply, so the charge may or may not exist. Asking again under the same key is
                // the only way to find out, and is exactly what the key is for.
                await Task.Delay(delays[attempt], ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && !last)
            {
                // A client-side timeout, not our caller giving up — the cancellation check separates
                // the two, so a shopper who navigated away is not retried on behalf of.
                await Task.Delay(delays[attempt], ct);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Out of attempts and still no answer from Stripe.
                return new SendOutcome(null, false, string.Empty, attempt + 1);
            }

            using (response)
            {
                if (IsTransient(response.StatusCode) && !last)
                {
                    // Stripe asks for Retry-After on a 429 to be honoured; ignoring it is how a
                    // rate-limited client turns a slowdown into a ban.
                    await Task.Delay(RetryAfter(response) ?? TimeSpan.FromMilliseconds(delays[attempt]), ct);
                    continue;
                }

                return new SendOutcome(
                    response.StatusCode,
                    response.IsSuccessStatusCode,
                    await response.Content.ReadAsStringAsync(ct),
                    attempt + 1);
            }
        }
    }

    /// <summary>The server's own requested wait, when it sent one and it is sane.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        if (delta is { } wait && wait > TimeSpan.Zero && wait <= TimeSpan.FromSeconds(5))
        {
            return wait;
        }

        // Anything longer is not worth holding a checkout request open for; the shopper is waiting.
        return null;
    }

    /// <summary>
    /// Statuses where the charge's outcome was not learned, so asking again may settle it. A 4xx is
    /// excluded deliberately: Stripe understood the request and answered it — a declined card is a
    /// decision, not a blip, and retrying one only annoys the issuer.
    /// </summary>
    private static bool IsTransient(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>Pulls next_action.redirect_to_url.url from a PaymentIntent, if present.</summary>
    private static string? ExtractRedirectUrl(JsonElement intent)
    {
        if (intent.TryGetProperty("next_action", out var nextAction) &&
            nextAction.ValueKind == JsonValueKind.Object &&
            nextAction.TryGetProperty("redirect_to_url", out var redirect) &&
            redirect.ValueKind == JsonValueKind.Object &&
            redirect.TryGetProperty("url", out var url))
        {
            return url.GetString();
        }

        return null;
    }
}
