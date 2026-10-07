namespace WidgetWorks.Domain.Users;

/// <summary>
/// The rules for a display name, in one place because three paths now set one: the profile form,
/// Google sign-in, and registration.
///
/// Sitting next to <see cref="PasswordPolicy"/> for the same reason that one exists — a rule copied
/// into three handlers is a rule that will hold in two of them.
/// </summary>
public static class DisplayNamePolicy
{
    /// <summary>
    /// Long enough for a real name, short enough that it cannot be used to stuff the header with
    /// someone's essay. Names are not usernames here — there is no uniqueness and nothing resolves
    /// by them, so the only limit that matters is display.
    /// </summary>
    public const int MaxLength = 60;

    /// <summary>
    /// Trims, and turns blank into <c>null</c>.
    ///
    /// The distinction matters twice over. The greeting falls back to a neutral one on null, so an
    /// empty string would render "Hello, " with nothing after it; and the database's check
    /// constraint rejects a blank name outright, so an empty string would fail the write rather
    /// than quietly store nothing.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>Whether a name a person typed is too long to accept.</summary>
    public static bool IsTooLong(string? name) => name is { Length: > MaxLength };

    /// <summary>The message shown when it is.</summary>
    public static string TooLongMessage => $"Name must be {MaxLength} characters or fewer.";

    /// <summary>
    /// Normalizes a name that came from an identity provider rather than from a person.
    ///
    /// Truncates instead of rejecting, which is the whole difference between this and the path a
    /// typed name takes. Someone whose Google profile name runs past our limit should still be able
    /// to sign in — refusing the login over a greeting would be absurd, and there is no form in
    /// front of them to correct it on. A typed name gets told; a supplied one gets trimmed.
    /// </summary>
    public static string? FromProvider(string? value)
    {
        var name = Normalize(value);
        if (name is null || name.Length <= MaxLength)
        {
            return name;
        }

        // TrimEnd so a cut landing mid-space does not leave a trailing one. It cannot empty the
        // string: Normalize already removed leading whitespace, so the first character is not one.
        return name[..MaxLength].TrimEnd();
    }
}
