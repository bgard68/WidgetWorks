using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.TwoFactor.Disable;

/// <summary>
/// <paramref name="ActorId"/> is whoever asked. The same as <paramref name="UserId"/> when someone
/// turns their own 2FA off; a different id when an administrator resets it for a customer who lost
/// their authenticator — and that difference is the whole reason the trail records both.
/// </summary>
public sealed record DisableTwoFactorCommand(Guid UserId, Guid? ActorId = null);

public sealed class DisableTwoFactorHandler(
    IUserRepository users,
    ITwoFactorRepository twoFactor,
    IAuditLog audit)
{
    public async Task<Result> Handle(DisableTwoFactorCommand command, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(command.UserId, ct);
        if (user is null)
        {
            return Result.Fail("User not found.");
        }

        await twoFactor.DeleteSecretAsync(user.Id, ct);
        await twoFactor.DeleteRecoveryCodesAsync(user.Id, ct);

        user.TwoFactorEnabled = false;
        user.SecurityStamp = Guid.NewGuid();
        await users.UpdateAsync(user, ct);
        // Named differently when it was not the account holder, because "an administrator cleared
        // this customer's second factor" is the entry someone will come looking for, and it must not
        // be indistinguishable from the customer doing it themselves.
        var byAdmin = command.ActorId is { } actor && actor != user.Id;
        await audit.WriteAsync(
            user.Id,
            byAdmin ? "2fa.reset_by_admin" : "2fa.disabled",
            byAdmin ? $"reset by {command.ActorId}" : null,
            ct);

        return Result.Success();
    }
}
