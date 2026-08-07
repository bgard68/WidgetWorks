using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.TwoFactor.Confirm;

public sealed record ConfirmEnrollCommand(Guid UserId, string Code);

public sealed record ConfirmEnrollResult(IReadOnlyList<string> RecoveryCodes);

public sealed class ConfirmEnrollHandler(
    IUserRepository users,
    ITwoFactorRepository twoFactor,
    ITotpService totp,
    IRecoveryCodes recovery,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<Result<ConfirmEnrollResult>> Handle(ConfirmEnrollCommand command, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(command.UserId, ct);
        if (user is null)
        {
            return Result<ConfirmEnrollResult>.Fail("User not found.");
        }

        var secret = await twoFactor.GetSecretAsync(user.Id, ct);
        if (secret is null)
        {
            return Result<ConfirmEnrollResult>.Fail("No pending 2FA enrollment. Start enrollment first.");
        }

        var now = clock.GetUtcNow();
        if (!totp.Verify(secret.Secret, command.Code, now))
        {
            return Result<ConfirmEnrollResult>.Fail("Invalid authenticator code.");
        }

        await twoFactor.MarkConfirmedAsync(user.Id, ct);

        var codes = recovery.Generate(10);
        await twoFactor.DeleteRecoveryCodesAsync(user.Id, ct);
        await twoFactor.AddRecoveryCodesAsync(user.Id, codes.Select(c => c.Hash).ToList(), now, ct);

        user.TwoFactorEnabled = true;
        user.SecurityStamp = Guid.NewGuid();   // security change -> invalidate other sessions
        await users.UpdateAsync(user, ct);
        await audit.WriteAsync(user.Id, "2fa.enabled", null, ct);

        return Result<ConfirmEnrollResult>.Success(new ConfirmEnrollResult(codes.Select(c => c.Plain).ToList()));
    }
}
