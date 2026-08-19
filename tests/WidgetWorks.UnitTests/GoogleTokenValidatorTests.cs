using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WidgetWorks.Infrastructure.Security;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Google ID-token validation, exercised end to end against a locally generated RSA key served as
/// a JWKS by a stub transport. This is the only place a stranger's assertion becomes an account, so
/// it is tested by forging tokens: right shape, wrong signer; right signer, wrong audience; right
/// everything, expired. Each must be refused, and the refusal must be silent (null) rather than an
/// exception the endpoint would have to interpret.
/// </summary>
public class GoogleTokenValidatorTests
{
    private const string ClientId = "866620806528-test.apps.googleusercontent.com";

    private static readonly RsaSecurityKey GoogleKey = new(RSA.Create(2048)) { KeyId = "test-key-1" };
    private static readonly RsaSecurityKey ImposterKey = new(RSA.Create(2048)) { KeyId = "test-key-1" };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Jwks(RsaSecurityKey key)
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        jwk.KeyId = key.KeyId;
        return JsonSerializer.Serialize(new { keys = new[] { jwk } });
    }

    private static string IdToken(
        RsaSecurityKey signer,
        string issuer = "https://accounts.google.com",
        string audience = ClientId,
        string? sub = "google-sub-123",
        string? email = "jane@example.com",
        bool emailVerified = true,
        string? name = "Jane Doe",
        int expiresInMinutes = 30)
    {
        var claims = new Dictionary<string, object>();
        if (sub is not null) claims["sub"] = sub;
        if (email is not null) claims["email"] = email;
        if (name is not null) claims["name"] = name;
        claims["email_verified"] = emailVerified;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(expiresInMinutes),
            SigningCredentials = new SigningCredentials(signer, SecurityAlgorithms.RsaSha256),
            Claims = claims,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static (GoogleTokenValidator Validator, StubJwks Jwks) Build(
        string? jwksBody = null,
        HttpStatusCode status = HttpStatusCode.OK,
        string clientId = ClientId)
    {
        var handler = new StubJwks(status, jwksBody ?? Jwks(GoogleKey));
        var validator = new GoogleTokenValidator(
            new HttpClient(handler),
            new GoogleOptions { ClientId = clientId },
            new FixedClock(DateTimeOffset.UtcNow));
        return (validator, handler);
    }

    [Fact]
    public async Task A_genuine_token_yields_the_identity()
    {
        var (validator, _) = Build();

        var identity = await validator.ValidateAsync(IdToken(GoogleKey), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("google-sub-123", identity!.Subject);
        Assert.Equal("jane@example.com", identity.Email);
        Assert.True(identity.EmailVerified);
        Assert.Equal("Jane Doe", identity.Name);
    }

    [Fact]
    public async Task A_token_signed_by_someone_else_is_refused()
    {
        var (validator, _) = Build();

        // Same key id, same claims, different private key — the whole point of checking signatures.
        var forged = IdToken(ImposterKey);

        Assert.Null(await validator.ValidateAsync(forged, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_for_another_application_is_refused()
    {
        var (validator, _) = Build();

        var otherApp = IdToken(GoogleKey, audience: "someone-elses-client-id.apps.googleusercontent.com");

        Assert.Null(await validator.ValidateAsync(otherApp, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_refused()
    {
        var (validator, _) = Build();

        Assert.Null(await validator.ValidateAsync(IdToken(GoogleKey, issuer: "https://evil.test"), CancellationToken.None));
    }

    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("accounts.google.com")]
    public async Task Both_issuer_spellings_google_uses_are_accepted(string issuer)
    {
        var (validator, _) = Build();

        Assert.NotNull(await validator.ValidateAsync(IdToken(GoogleKey, issuer: issuer), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (validator, _) = Build();

        Assert.Null(await validator.ValidateAsync(IdToken(GoogleKey, expiresInMinutes: -30), CancellationToken.None));
    }

    [Fact]
    public async Task A_token_without_an_email_is_refused()
    {
        var (validator, _) = Build();

        // The app keys accounts on email; an identity without one cannot be provisioned.
        Assert.Null(await validator.ValidateAsync(IdToken(GoogleKey, email: null), CancellationToken.None));
    }

    [Fact]
    public async Task An_unverified_email_is_returned_but_flagged()
    {
        var (validator, _) = Build();

        var identity = await validator.ValidateAsync(IdToken(GoogleKey, emailVerified: false), CancellationToken.None);

        // The validator reports; the login handler decides. Refusing here would hide the reason.
        Assert.NotNull(identity);
        Assert.False(identity!.EmailVerified);
    }

    [Fact]
    public async Task A_token_with_no_name_still_validates()
    {
        var (validator, _) = Build();

        var identity = await validator.ValidateAsync(IdToken(GoogleKey, name: null), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Null(identity!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    public async Task Garbage_is_refused_without_calling_google(string token)
    {
        var (validator, jwks) = Build();

        Assert.Null(await validator.ValidateAsync(token, CancellationToken.None));
        if (string.IsNullOrWhiteSpace(token))
        {
            Assert.Equal(0, jwks.Calls);
        }
    }

    [Fact]
    public async Task Google_sign_in_is_off_when_no_client_id_is_configured()
    {
        var (validator, jwks) = Build(clientId: "");

        Assert.Null(await validator.ValidateAsync(IdToken(GoogleKey), CancellationToken.None));
        Assert.Equal(0, jwks.Calls);
    }

    [Fact]
    public async Task An_unreachable_key_endpoint_refuses_rather_than_throws()
    {
        var (validator, _) = Build(status: HttpStatusCode.ServiceUnavailable, jwksBody: "unavailable");

        // Google being down must not surface as a 500 from our login endpoint.
        Assert.Null(await validator.ValidateAsync(IdToken(GoogleKey), CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_key_material_refuses_rather_than_throws()
    {
        var (validator, _) = Build(jwksBody: "{ not json");

        Assert.Null(await validator.ValidateAsync(IdToken(GoogleKey), CancellationToken.None));
    }

    [Fact]
    public async Task The_key_set_is_fetched_once_and_reused()
    {
        var (validator, jwks) = Build();

        await validator.ValidateAsync(IdToken(GoogleKey), CancellationToken.None);
        await validator.ValidateAsync(IdToken(GoogleKey), CancellationToken.None);
        await validator.ValidateAsync(IdToken(GoogleKey), CancellationToken.None);

        // Google's keys rotate slowly; refetching per sign-in would be a self-inflicted rate limit.
        Assert.Equal(1, jwks.Calls);
    }

    private sealed class StubJwks(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
