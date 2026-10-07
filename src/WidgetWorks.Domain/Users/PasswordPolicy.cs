namespace WidgetWorks.Domain.Users;

/// <summary>One rule a password has to satisfy, and the words shown to whoever has to satisfy it.</summary>
/// <param name="Code">Stable identifier a client can match on without parsing English.</param>
/// <param name="Requirement">The rule as a person reads it, e.g. "At least 10 characters".</param>
public sealed record PasswordRule(string Code, string Requirement, Func<string, bool> IsMet);

/// <summary>
/// What makes a password acceptable, in one place, because two places would eventually disagree.
///
/// This lives in the domain rather than next to either use case: registration and password reset both
/// set a password, and a rule enforced on one path and not the other is the hole someone walks
/// through — reset was in fact the softer of the two before this existed.
///
/// The rules are composition requirements (length plus character classes). Modern guidance prefers
/// length and a breach-list check over composition rules, and that would be the better policy — but it
/// needs a breach corpus this project has no business shipping, and composition rules are what a
/// shopper recognises from every other checkout. The honest summary: these stop the careless password,
/// not the determined attacker. Rate limiting, lockout and the security stamp are what handle the
/// attacker, and they live elsewhere.
///
/// The web client mirrors this list to tick requirements off as someone types
/// (<c>web/src/lib/passwordPolicy.ts</c>). That copy is a convenience; this is the authority, and
/// every rule here is enforced server-side so a client that skips the checklist gains nothing.
/// </summary>
public static class PasswordPolicy
{
    /// <summary>
    /// Ten, not eight. Length is the one factor that reliably costs an attacker more, and the two extra
    /// characters are free to a password manager and barely felt by anyone else. Every seeded demo
    /// account already satisfies it.
    /// </summary>
    public const int MinimumLength = 10;

    /// <summary>
    /// The rules, in the order they are shown. Order matters only for presentation — a checklist that
    /// reshuffles as you type is harder to read than the thing it is explaining.
    /// </summary>
    public static readonly IReadOnlyList<PasswordRule> Rules =
    [
        new("length", $"At least {MinimumLength} characters", p => p.Length >= MinimumLength),
        new("lowercase", "A lowercase letter", p => p.Any(char.IsLower)),
        new("uppercase", "An uppercase letter", p => p.Any(char.IsUpper)),
        new("digit", "A number", p => p.Any(char.IsDigit)),

        // Anything that is not a letter, a digit or whitespace. Defined by exclusion on purpose: an
        // allow-list of symbols would quietly reject a perfectly good passphrase character that nobody
        // thought to list, and the rule only exists to widen the alphabet.
        new("symbol", "A symbol, such as ! ? # or @", p => p.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c))),
    ];

    /// <summary>
    /// The rules this password fails, in presentation order. Empty means acceptable.
    ///
    /// A null or whitespace-only password fails everything rather than being special-cased, which is
    /// both true and more useful than one generic complaint.
    /// </summary>
    public static IReadOnlyList<PasswordRule> Unmet(string? password)
    {
        var candidate = password ?? string.Empty;

        // Deliberately not trimmed. Leading and trailing spaces are legitimate password characters and
        // trimming them would silently change what someone typed — and then fail to match it at login.
        return string.IsNullOrWhiteSpace(candidate)
            ? Rules
            : Rules.Where(rule => !rule.IsMet(candidate)).ToList();
    }

    public static bool IsAcceptable(string? password) => Unmet(password).Count == 0;

    /// <summary>
    /// One sentence naming what is missing, for an API error.
    ///
    /// It lists the unmet rules rather than restating the whole policy, because "must contain a number"
    /// is actionable and "must be 10 characters with upper and lower case and a number and a symbol" is
    /// a wall someone has to diff against what they typed. Safe to return: it describes our rules, not
    /// anything about the password or the account.
    /// </summary>
    public static string Describe(string? password)
    {
        var unmet = Unmet(password);
        return unmet.Count == 0
            ? string.Empty
            : "Password needs: " + string.Join(", ", unmet.Select(r => r.Requirement.ToLowerInvariant())) + ".";
    }
}
