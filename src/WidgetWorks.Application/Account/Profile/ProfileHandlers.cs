using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Account.Profile;

/// <summary>
/// What the account area shows about the person signed in.
///
/// Email and member-since are read-only facts; the display name is the only thing editable here.
/// Email deliberately is not: changing it safely needs a confirmation sent to the *new* address
/// before the switch, or it becomes an account-takeover step rather than a profile edit.
/// </summary>
public sealed record ProfileView(
    string Email,
    string? DisplayName,
    string Role,
    bool TwoFactorEnabled,
    bool HasPassword,
    DateTimeOffset MemberSince);

public sealed record GetProfileQuery(Guid UserId);

public sealed class GetProfileHandler(IUserRepository users)
{
    public async Task<Result<ProfileView>> Handle(GetProfileQuery query, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(query.UserId, ct);
        if (user is null)
        {
            return Result<ProfileView>.Fail("User not found.");
        }

        // HasPassword tells the client whether to offer a password change at all: a Google-only
        // account has nothing to change, and a disabled button with no explanation is worse than
        // an absent one.
        return Result<ProfileView>.Success(new ProfileView(
            user.Email,
            user.DisplayName,
            user.Role,
            user.TwoFactorEnabled,
            user.PasswordHash is not null,
            user.CreatedAt));
    }
}

public sealed record UpdateProfileCommand(Guid UserId, string? DisplayName);

/// <summary>
/// Sets or clears the display name. The only editable field on the profile, by design — see
/// <see cref="ProfileView"/> for why email is not.
/// </summary>
public sealed class UpdateProfileHandler(IUserRepository users, IAuditLog audit)
{
    /// <summary>
    /// Long enough for a real name, short enough that it cannot be used to stuff the header with
    /// someone's essay. Names are not usernames here — there is no uniqueness and nothing resolves
    /// by them, so the only limit that matters is display.
    /// </summary>
    public const int MaxLength = DisplayNamePolicy.MaxLength;

    public async Task<Result<ProfileView>> Handle(UpdateProfileCommand command, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(command.UserId, ct);
        if (user is null)
        {
            return Result<ProfileView>.Fail("User not found.");
        }

        var name = DisplayNamePolicy.Normalize(command.DisplayName);
        if (DisplayNamePolicy.IsTooLong(name))
        {
            return Result<ProfileView>.Fail(DisplayNamePolicy.TooLongMessage);
        }

        user.DisplayName = name;
        await users.UpdateAsync(user, ct);

        // Recorded without the value. Who changed their own name is worth knowing; keeping a second
        // copy of it in the audit trail is not.
        await audit.WriteAsync(user.Id, name is null ? "profile.name_cleared" : "profile.name_set", null, ct);

        return Result<ProfileView>.Success(new ProfileView(
            user.Email, user.DisplayName, user.Role, user.TwoFactorEnabled, user.PasswordHash is not null, user.CreatedAt));
    }
}
