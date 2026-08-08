using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Auth.PasswordReset;

public sealed record RequestPasswordResetCommand(string Email);

/// <summary>
/// Starts a password reset: for an existing (non-protected) account, issues a single-use token and
/// emails a link. Always returns success so callers can't enumerate which emails have accounts.
/// </summary>
public sealed class RequestPasswordResetHandler(
    IUserRepository users,
    IPasswordResetTokenRepository tokens,
    ISecureTokenGenerator generator,
    IEmailSender email,
    AppOptions app,
    TimeProvider clock)
{
    public async Task<Result> Handle(RequestPasswordResetCommand command, CancellationToken ct)
    {
        var normalized = (command.Email ?? string.Empty).Trim().ToUpperInvariant();
        var user = normalized.Length == 0 ? null : await users.GetByNormalizedEmailAsync(normalized, ct);

        // The protected demo admin's credentials are immutable, so it never gets a reset token.
        if (user is not null && !user.IsProtectedAdmin)
        {
            var now = clock.GetUtcNow();
            await tokens.InvalidateForUserAsync(user.Id, now, ct);

            var raw = generator.Generate();
            var token = new PasswordResetToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = generator.Hash(raw),
                ExpiresAt = now.AddMinutes(30),
                CreatedAt = now,
            };
            await tokens.AddAsync(token, ct);

            var link = $"{app.BaseUrl.TrimEnd('/')}/reset-password?token={raw}";
            try
            {
                await email.SendAsync(AccountEmailTemplates.PasswordReset(user.Email, link), ct);
            }
            catch
            {
                // Best-effort; still return success to avoid leaking account existence.
            }
        }

        return Result.Success();
    }
}
