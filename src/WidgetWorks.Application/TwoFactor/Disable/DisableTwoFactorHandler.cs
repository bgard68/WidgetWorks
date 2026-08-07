using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.TwoFactor.Disable;

public sealed record DisableTwoFactorCommand(Guid UserId);

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
        await audit.WriteAsync(user.Id, "2fa.disabled", null, ct);

        return Result.Success();
    }
}
