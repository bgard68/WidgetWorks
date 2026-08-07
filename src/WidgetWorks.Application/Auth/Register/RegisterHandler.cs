using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Auth.Register;

public sealed record RegisterCommand(string Email, string Password);

public sealed class RegisterHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<Result> Handle(RegisterCommand command, CancellationToken ct)
    {
        var email = (command.Email ?? string.Empty).Trim();
        if (!email.Contains('@'))
        {
            return Result.Fail("A valid email is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Password) || command.Password.Length < 8)
        {
            return Result.Fail("Password must be at least 8 characters.");
        }

        var normalized = email.ToUpperInvariant();
        if (await users.GetByNormalizedEmailAsync(normalized, ct) is not null)
        {
            // Non-enumerating: deliberately generic.
            return Result.Fail("Unable to register with the provided details.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = normalized,
            PasswordHash = hasher.Hash(command.Password),
            Role = UserRoles.Customer,
            SecurityStamp = Guid.NewGuid(),
            CreatedAt = clock.GetUtcNow(),
        };

        await users.AddAsync(user, ct);
        return Result.Success();
    }
}
