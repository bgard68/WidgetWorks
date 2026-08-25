using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Auth.Register;

public sealed record RegisterCommand(string Email, string Password);

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

        if (string.IsNullOrWhiteSpace(command.Password) || command.Password.Length < 8)
        {
            return Result.Fail("Password must be at least 8 characters.");
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
