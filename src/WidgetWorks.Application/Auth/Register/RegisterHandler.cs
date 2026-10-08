using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Auth.Register;

/// <summary>
/// <paramref name="DisplayName"/> is optional and defaults to null, which keeps every existing
/// caller — and the posted JSON of anyone who does not send it — compiling and working unchanged.
/// </summary>
public sealed record RegisterCommand(string Email, string Password, string? DisplayName = null);

public sealed class RegisterHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IEmailSender email,
    TimeProvider clock,
    ILogger<RegisterHandler> logger)
{
    public async Task<Result> Handle(RegisterCommand command, CancellationToken ct)
    {
        var emailAddress = (command.Email ?? string.Empty).Trim();
        if (!emailAddress.Contains('@'))
        {
            return Result.Fail("A valid email is required.");
        }

        // One policy, shared with password reset, so neither path can be the soft way in.
        if (!PasswordPolicy.IsAcceptable(command.Password))
        {
            return Result.Fail(PasswordPolicy.Describe(command.Password));
        }

        // Checked before the duplicate-email test, so a name that is too long is reported as the
        // specific thing it is. After it, the deliberately vague "unable to register" would win and
        // a fixable mistake would read as a rejected account.
        var displayName = DisplayNamePolicy.Normalize(command.DisplayName);
        if (DisplayNamePolicy.IsTooLong(displayName))
        {
            return Result.Fail(DisplayNamePolicy.TooLongMessage);
        }

        var normalized = emailAddress.ToUpperInvariant();
        if (await users.GetByNormalizedEmailAsync(normalized, ct) is not null)
        {
            // Non-enumerating: deliberately generic.
            return Result.Fail("Unable to register with the provided details.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = emailAddress,
            NormalizedEmail = normalized,
            PasswordHash = hasher.Hash(command.Password),
            Role = UserRoles.Customer,
            SecurityStamp = Guid.NewGuid(),
            DisplayName = displayName,
            CreatedAt = clock.GetUtcNow(),
        };

        await users.AddAsync(user, ct);

        try
        {
            await email.SendAsync(AccountEmailTemplates.Welcome(user.Email), ct);
        }
        catch (Exception ex)
        {
            // Never fail registration on a notification error - the account exists
            // either way. Logged so a mail outage does not look like nothing happened.
            logger.LogWarning(
                ex,
                "Welcome email failed for new user {UserId}; the account stands.",
                user.Id);
        }

        return Result.Success();
    }
}
