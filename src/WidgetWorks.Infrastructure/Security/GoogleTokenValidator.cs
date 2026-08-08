using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Security;

public sealed class GoogleOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string JwksUri { get; set; } = "https://www.googleapis.com/oauth2/v3/certs";
}

/// <summary>
/// Validates a Google ID token against Google's published JWKS: signature, issuer
/// (accounts.google.com), audience (our client id), and lifetime. Signing keys are cached for ~1 hour.
/// Uses the already-referenced Microsoft.IdentityModel stack -- no extra dependency.
/// </summary>
public sealed class GoogleTokenValidator(HttpClient http, GoogleOptions options, TimeProvider clock) : IGoogleTokenValidator
{
    private static readonly string[] ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];

    private JsonWebKeySet? _keys;
    private DateTimeOffset _keysFetchedAt;

    public async Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var keys = await GetKeysAsync(ct);
        if (keys is null)
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ValidIssuers,
            ValidateAudience = true,
            ValidAudience = options.ClientId,
            ValidateLifetime = true,
            IssuerSigningKeys = keys.GetSigningKeys(),
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        if (!jwt.TryGetPayloadValue<string>("sub", out var sub) || string.IsNullOrEmpty(sub))
        {
            return null;
        }

        jwt.TryGetPayloadValue<string>("email", out var emailValue);
        if (string.IsNullOrEmpty(emailValue))
        {
            return null;
        }

        jwt.TryGetPayloadValue<bool>("email_verified", out var verified);
        jwt.TryGetPayloadValue<string>("name", out var name);

        return new GoogleIdentity(sub, emailValue, verified, name);
    }

    private async Task<JsonWebKeySet?> GetKeysAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_keys is not null && now - _keysFetchedAt < TimeSpan.FromHours(1))
        {
            return _keys;
        }

        try
        {
            var json = await http.GetStringAsync(options.JwksUri, ct);
            _keys = new JsonWebKeySet(json);
            _keysFetchedAt = now;
            return _keys;
        }
        catch
        {
            return _keys;   // fall back to cached keys if the refresh fails
        }
    }
}
