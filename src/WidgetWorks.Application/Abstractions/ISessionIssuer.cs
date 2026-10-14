using WidgetWorks.Application.Auth;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Abstractions;

/// <summary>
/// Starts a new signed-in session: mints the access token, mints and stores the refresh token, and
/// returns the pair.
///
/// This exists because five handlers were each doing it themselves, in fourteen identical lines —
/// password login, Google login, the 2FA step, recovery-code login, and changing a password. The
/// duplication was not merely untidy. Session issuance is the security boundary of the whole
/// application, and spreading it across five files means every change to it has to be made five
/// times: binding a session to a device, shortening a refresh lifetime, recording the origin of a
/// login. Miss one and the result is not a compile error but a single sign-in route that behaves
/// differently from the rest, which is exactly the shape of an authentication hole.
///
/// Deliberately **not** responsible for refresh rotation. <c>RefreshHandler</c> keeps that, because
/// rotating inside an existing family carries reuse detection and revocation of the token being
/// replaced — that is a policy decision about an existing session, not the creation of a new one.
/// Folding it in here would put two different operations behind one name.
/// </summary>
public interface ISessionIssuer
{
    /// <summary>
    /// Issues a brand-new session for <paramref name="user"/>, starting a fresh token family.
    ///
    /// A new family rather than a continued one is the point: every caller here has just finished
    /// authenticating someone, so whatever sessions existed before are unrelated to this one.
    /// </summary>
    Task<AuthResponse> IssueAsync(User user, DateTimeOffset now, CancellationToken ct);
}
