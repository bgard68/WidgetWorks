namespace WidgetWorks.Domain.Users;

/// <summary>Thrown when an operation would alter or remove the immutable seeded administrator.</summary>
public sealed class ProtectedAdminException(string message) : InvalidOperationException(message);

/// <summary>
/// Domain rule: the seeded demo administrator's identity (email, role, existence) is immutable so
/// the showcase always has a working super-admin. Security operations that do not change identity
/// (session/stamp rotation, lockout counters, 2FA toggles) remain allowed. Enforced again at the
/// data layer by a database trigger, per defense-in-depth.
/// </summary>
public static class ProtectedAdminGuard
{
    public static void EnsureCanDelete(User user)
    {
        if (user.IsProtectedAdmin)
        {
            throw new ProtectedAdminException("The protected administrator account cannot be deleted.");
        }
    }

    public static void EnsureCanChangeIdentity(User current, string newEmail, string newRole)
    {
        if (!current.IsProtectedAdmin)
        {
            return;
        }

        var emailChanged = !string.Equals(current.Email, newEmail, StringComparison.OrdinalIgnoreCase);
        var roleChanged = !string.Equals(current.Role, newRole, StringComparison.Ordinal);
        if (emailChanged || roleChanged)
        {
            throw new ProtectedAdminException("The protected administrator's email and role cannot be changed.");
        }
    }
}
