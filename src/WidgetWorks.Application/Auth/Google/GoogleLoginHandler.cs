using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Auth.Google;

public sealed record GoogleLoginCommand(string IdToken);

/// <summary>
/// Signs a user in with a Google ID token: validates it, then finds by google_sub, links Google to an
/// existing email, or provisions a new Customer (no password) -- and issues our access + refresh tokens.
/// </summary>
public sealed class GoogleLoginHandler(
    IGoogleTokenValidator google,
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    IAuditLog audit,
    IEmailSender email,
    TimeProvider clock,
    ILogger<GoogleLoginHandler> logger)
{
    public async Task<Result<AuthResponse>> Handle(GoogleLoginCommand command, CancellationToken ct)
    {
        var identity = await google.ValidateAsync(command.IdToken ?? string.Empty, ct);
        if (identity is null)
        {
            return Result<AuthResponse>.Fail("Google sign-in failed.");
        }

        if (!identity.EmailVerified)
        {
            return Result<AuthResponse>.Fail("Your Google email address is not verified.");
        }

        var now = clock.GetUtcNow();
        var user = await users.GetByGoogleSubAsync(identity.Subject, ct);
        if (user is null)
        {
            var normalized = identity.Email.Trim().ToUpperInvariant();
            user = await users.GetByNormalizedEmailAsync(normalized, ct);
            if (user is not null)
            {
                user.GoogleSub = identity.Subject;
                await users.UpdateAsync(user, ct);
                await audit.WriteAsync(user.Id, "login.google_linked", null, ct);
            }
            else
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Email = identity.Email.Trim(),
                    NormalizedEmail = normalized,
                    PasswordHash = null,
                    Role = UserRoles.Customer,
                    SecurityStamp = Guid.NewGuid(),
                    GoogleSub = identity.Subject,
                    CreatedAt = now,
                };
                await users.AddAsync(user, ct);
                await audit.WriteAsync(user.Id, "login.google_signup", null, ct);
                try
                {
                    await email.SendAsync(AccountEmailTemplates.Welcome(user.Email), ct);
                }
                catch (Exception ex)
                {
                    // Best-effort: a dead mail server must not cost someone their first
                    // sign-in. Logged so the outage is visible rather than silent.
                    logger.LogWarning(
                        ex,
                        "Welcome email failed for new Google user {UserId}; the account stands.",
                        user.Id);
                }
            }
        }

        if (user.IsLockedOut(now))
        {
            return Result<AuthResponse>.Fail("Account is temporarily locked. Try again later.");
        }

        var access = tokens.CreateAccessToken(user);
        var refresh = tokens.CreateRefreshToken(Guid.NewGuid());
        await refreshTokens.AddAsync(
            new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = refresh.Hash,
                FamilyId = refresh.FamilyId,
                ExpiresAt = refresh.ExpiresAt,
                CreatedAt = now,
            },
            ct);

        await audit.WriteAsync(user.Id, "login.google_success", null, ct);
        return Result<AuthResponse>.Success(new AuthResponse(access.Value, access.ExpiresAt, refresh.Value, refresh.ExpiresAt, user.Role));
    }
}
