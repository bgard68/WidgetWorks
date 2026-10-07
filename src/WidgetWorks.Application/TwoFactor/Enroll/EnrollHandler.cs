using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.TwoFactor.Enroll;

public sealed record EnrollCommand(Guid UserId);

public sealed record EnrollResult(string SecretBase32, string OtpAuthUri);

public sealed class EnrollHandler(
    IUserRepository users,
    ITwoFactorRepository twoFactor,
    ITotpService totp)
{
    public async Task<Result<EnrollResult>> Handle(EnrollCommand command, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(command.UserId, ct);
        if (user is null)
        {
            return Result<EnrollResult>.Fail("User not found.");
        }

        // Refused before a secret is minted, so nothing half-enrolled is left behind.
        if (user.IsProtectedAdmin)
        {
            return Result<EnrollResult>.Fail(
                "Two-factor authentication cannot be enabled on a shared demo account. " +
                "Register your own account to try it.");
        }

        var secret = totp.CreateSecret(user.Email);
        await twoFactor.UpsertPendingSecretAsync(user.Id, secret.SecretBase32, ct);
        return Result<EnrollResult>.Success(new EnrollResult(secret.SecretBase32, secret.OtpAuthUri));
    }
}
