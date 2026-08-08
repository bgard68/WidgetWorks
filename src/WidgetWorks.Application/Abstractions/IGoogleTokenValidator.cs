namespace WidgetWorks.Application.Abstractions;

/// <summary>The verified identity extracted from a Google ID token.</summary>
public sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string? Name);

/// <summary>Validates a Google ID token (signature, issuer, audience, lifetime) and returns its identity.</summary>
public interface IGoogleTokenValidator
{
    Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken ct);
}
