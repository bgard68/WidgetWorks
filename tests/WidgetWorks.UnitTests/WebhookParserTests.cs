using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Infrastructure.Payments;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Webhook verification and parsing. This is the app's only unauthenticated write path — anyone on
/// the internet can POST to it — so the signature check is the security boundary, and every way it
/// can be fooled is worth a test: no header, no signature, a signature for a different payload, a
/// signature for a different secret.
/// </summary>
public class WebhookParserTests
{
    private const string Secret = "whsec_test_do_not_use";

    private static StripePaymentWebhookParser Stripe(string secret = Secret)
        => new(Options.Create(new StripeOptions { WebhookSecret = secret }));

    private static MockPaymentWebhookParser Mock(string secret = "")
        => new(Options.Create(new MockPaymentOptions { WebhookSecret = secret }));

    /// <summary>Builds the header Stripe would send: t=timestamp, v1=HMAC-SHA256 of "{t}.{payload}".</summary>
    private static string SignatureFor(string payload, string secret = Secret, string timestamp = "1735689600")
    {
        var signed = $"{timestamp}.{payload}";
        var hex = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed)));
        return $"t={timestamp},v1={hex}";
    }

    // Built by substitution rather than interpolation: the JSON ends in three braces, which an
    // interpolated raw string reads as an interpolation hole.
    private static string Intent(string type, string id = "pi_3ABC123") =>
        """{"type":"__TYPE__","data":{"object":{"id":"__ID__","object":"payment_intent"}}}"""
            .Replace("__TYPE__", type)
            .Replace("__ID__", id);

    // ---- stripe: signature ---------------------------------------------------------------

    [Fact]
    public void Stripe_refuses_to_run_without_a_configured_secret()
    {
        var payload = Intent("payment_intent.succeeded");

        var ok = Stripe(secret: "").TryParse(payload, SignatureFor(payload), out var evt, out var error);

        // Fail closed: an unconfigured secret must never mean "accept everything".
        Assert.False(ok);
        Assert.Null(evt);
        Assert.Equal("Stripe webhook secret is not configured.", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("t=1735689600")]                 // timestamp but no signature
    [InlineData("v1=abc123")]                    // signature but no timestamp
    [InlineData("t=1735689600,v1")]              // malformed pair, skipped -> no signatures left
    public void Stripe_rejects_a_header_it_cannot_verify(string? header)
    {
        var payload = Intent("payment_intent.succeeded");

        var ok = Stripe().TryParse(payload, header, out var evt, out var error);

        Assert.False(ok);
        Assert.Null(evt);
        Assert.Equal("Invalid webhook signature.", error);
    }

    [Fact]
    public void Stripe_rejects_a_signature_made_with_a_different_secret()
    {
        var payload = Intent("payment_intent.succeeded");

        var ok = Stripe().TryParse(payload, SignatureFor(payload, secret: "whsec_someone_elses"), out _, out var error);

        Assert.False(ok);
        Assert.Equal("Invalid webhook signature.", error);
    }

    [Fact]
    public void Stripe_rejects_a_valid_signature_for_a_different_payload()
    {
        var signed = Intent("payment_intent.succeeded", "pi_ORIGINAL");
        var tampered = Intent("payment_intent.succeeded", "pi_ATTACKER");

        // The signature is genuine — but for the body the attacker replaced.
        var ok = Stripe().TryParse(tampered, SignatureFor(signed), out _, out var error);

        Assert.False(ok);
        Assert.Equal("Invalid webhook signature.", error);
    }

    [Fact]
    public void Stripe_rejects_a_signature_bound_to_a_different_timestamp()
    {
        var payload = Intent("payment_intent.succeeded");
        var header = SignatureFor(payload, timestamp: "1735689600").Replace("t=1735689600", "t=1735689999");

        var ok = Stripe().TryParse(payload, header, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Invalid webhook signature.", error);
    }

    [Fact]
    public void Stripe_accepts_the_correct_signature()
    {
        var payload = Intent("payment_intent.succeeded");

        var ok = Stripe().TryParse(payload, SignatureFor(payload), out var evt, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("Stripe", evt!.Provider);
        Assert.Equal("pi_3ABC123", evt.Reference);
        Assert.Equal(PaymentEventType.Succeeded, evt.Type);
    }

    [Fact]
    public void Stripe_accepts_when_one_of_several_signatures_matches()
    {
        var payload = Intent("payment_intent.succeeded");
        var real = SignatureFor(payload);

        // During a secret rotation Stripe sends more than one v1.
        var header = real + ",v1=" + new string('a', 64);

        Assert.True(Stripe().TryParse(payload, header, out _, out _));
    }

    [Fact]
    public void Stripe_accepts_an_uppercase_hex_signature()
    {
        var payload = Intent("payment_intent.succeeded");
        var header = SignatureFor(payload).ToUpperInvariant().Replace("T=", "t=").Replace("V1=", "v1=");

        Assert.True(Stripe().TryParse(payload, header, out _, out _));
    }

    [Fact]
    public void Stripe_rejects_a_signature_of_the_wrong_length_without_throwing()
    {
        var payload = Intent("payment_intent.succeeded");

        var ok = Stripe().TryParse(payload, "t=1735689600,v1=abcd", out _, out var error);

        Assert.False(ok);
        Assert.Equal("Invalid webhook signature.", error);
    }

    // ---- stripe: event mapping -----------------------------------------------------------

    [Theory]
    [InlineData("payment_intent.succeeded", PaymentEventType.Succeeded)]
    [InlineData("payment_intent.payment_failed", PaymentEventType.Failed)]
    [InlineData("payment_intent.canceled", PaymentEventType.Failed)]
    public void Stripe_maps_the_intent_events_it_cares_about(string type, PaymentEventType expected)
    {
        var payload = Intent(type);

        var ok = Stripe().TryParse(payload, SignatureFor(payload), out var evt, out _);

        Assert.True(ok);
        Assert.Equal(expected, evt!.Type);
    }

    [Theory]
    [InlineData("charge.refunded")]
    [InlineData("customer.created")]
    [InlineData("")]
    public void Stripe_declines_events_it_does_not_handle(string type)
    {
        var payload = Intent(type);

        var ok = Stripe().TryParse(payload, SignatureFor(payload), out var evt, out var error);

        Assert.False(ok);
        Assert.Null(evt);
        Assert.Contains("Unhandled event type", error);
    }

    [Theory]
    [InlineData("""{"type":"payment_intent.succeeded"}""")]
    [InlineData("""{"type":"payment_intent.succeeded","data":{}}""")]
    [InlineData("""{"type":"payment_intent.succeeded","data":{"object":{}}}""")]
    [InlineData("""{"type":"payment_intent.succeeded","data":{"object":{"id":""}}}""")]
    public void Stripe_requires_a_payment_intent_id(string payload)
    {
        var ok = Stripe().TryParse(payload, SignatureFor(payload), out var evt, out var error);

        Assert.False(ok);
        Assert.Null(evt);
        Assert.Equal("Missing PaymentIntent id.", error);
    }

    [Fact]
    public void Stripe_reports_malformed_json_rather_than_throwing()
    {
        const string payload = "{not json";

        var ok = Stripe().TryParse(payload, SignatureFor(payload), out var evt, out var error);

        Assert.False(ok);
        Assert.Null(evt);
        Assert.Equal("Malformed webhook payload.", error);
    }

    // ---- mock provider -------------------------------------------------------------------

    [Fact]
    public void Mock_skips_verification_when_no_secret_is_configured()
    {
        var ok = Mock().TryParse("""{"reference":"mock_pi_1","outcome":"succeeded"}""", null, out var evt, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("Mock", evt!.Provider);
        Assert.Equal("mock_pi_1", evt.Reference);
    }

    [Fact]
    public void Mock_enforces_the_shared_secret_once_one_is_configured()
    {
        var parser = Mock(secret: "shhh");

        Assert.False(parser.TryParse("""{"reference":"mock_pi_1"}""", "wrong", out _, out var error));
        Assert.Equal("Invalid webhook signature.", error);

        Assert.False(parser.TryParse("""{"reference":"mock_pi_1"}""", null, out _, out _));
        Assert.True(parser.TryParse("""{"reference":"mock_pi_1"}""", "shhh", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Mock_rejects_an_empty_body(string payload)
    {
        var ok = Mock().TryParse(payload, null, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Empty webhook payload.", error);
    }

    [Theory]
    [InlineData("""{"outcome":"succeeded"}""")]
    [InlineData("""{"reference":""}""")]
    [InlineData("""{"reference":"   "}""")]
    [InlineData("""{"reference":null}""")]
    public void Mock_requires_a_reference(string payload)
    {
        var ok = Mock().TryParse(payload, null, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Missing 'reference'.", error);
    }

    [Theory]
    [InlineData("""{"reference":"r","outcome":"failed"}""", PaymentEventType.Failed)]
    [InlineData("""{"reference":"r","outcome":"FAILED"}""", PaymentEventType.Failed)]
    [InlineData("""{"reference":"r","outcome":"succeeded"}""", PaymentEventType.Succeeded)]
    [InlineData("""{"reference":"r"}""", PaymentEventType.Succeeded)]
    [InlineData("""{"reference":"r","outcome":"anything-else"}""", PaymentEventType.Succeeded)]
    public void Mock_treats_only_an_explicit_failure_as_a_failure(string payload, PaymentEventType expected)
    {
        var ok = Mock().TryParse(payload, null, out var evt, out _);

        Assert.True(ok);
        Assert.Equal(expected, evt!.Type);
    }

    [Fact]
    public void Mock_reports_malformed_json_rather_than_throwing()
    {
        var ok = Mock().TryParse("{oops", null, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Malformed webhook payload.", error);
    }

    [Fact]
    public void Parsers_declare_the_provider_key_the_route_matches_on()
    {
        Assert.Equal("Stripe", Stripe().Provider);
        Assert.Equal("Mock", Mock().Provider);
    }
}
