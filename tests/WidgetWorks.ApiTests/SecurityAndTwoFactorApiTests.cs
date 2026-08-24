using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OtpNet;
using Xunit;

namespace WidgetWorks.ApiTests;

[Collection(ApiCollection.Name)]
public class SecurityAndTwoFactorApiTests(ApiFixture api)
{
    /// <summary>The code the enrolled authenticator app would show right now.</summary>
    private static string CurrentCode(string secretBase32)
        => new Totp(Base32Encoding.ToBytes(secretBase32)).ComputeTotp(DateTime.UtcNow);

    [Fact]
    public async Task Securing_an_account_kills_every_outstanding_token()
    {
        var (client, _) = await api.FreshCustomerAsync();
        using var _client = client;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/orders")).StatusCode);

        var secured = await client.PostAsync("/auth/secure-account", null);
        Assert.Equal(HttpStatusCode.NoContent, secured.StatusCode);

        // Same bearer token, now refused: the security stamp rotated underneath it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/orders")).StatusCode);
    }

    [Fact]
    public async Task An_administrator_can_revoke_a_users_sessions_a_customer_cannot()
    {
        var (victim, email) = await api.FreshCustomerAsync();
        using var _victim = victim;

        using var customer = await api.SignedInAsync(ApiFixture.CustomerEmail);
        // The route itself is Administrator-only.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.PostAsync($"/admin/users/{Guid.NewGuid()}/revoke-sessions", null)).StatusCode);

        using var admin = await api.SignedInAsync(ApiFixture.AdminEmail);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsync($"/admin/users/{Guid.NewGuid()}/revoke-sessions", null)).StatusCode);

        // Find the victim's id through their own orders? Simpler: revoke by logging in and reading 'sub'
        // is internal, so drive the observable effect instead — the victim's token dies.
        var victimId = ReadSubClaim(victim.DefaultRequestHeaders.Authorization!.Parameter!);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/admin/users/{victimId}/revoke-sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/orders")).StatusCode);
        _ = email;
    }

    [Fact]
    public async Task The_full_two_factor_story_enroll_confirm_challenge_recover_disable()
    {
        var (client, email) = await api.FreshCustomerAsync();
        using var _client = client;

        // Enroll: the server hands back the shared secret as an otpauth URI.
        var enroll = await client.PostAsync("/2fa/enroll", null);
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var enrollBody = await enroll.Content.ReadFromJsonAsync<JsonElement>();
        var secret = enrollBody.GetProperty("secretBase32").GetString()!;
        Assert.Contains("otpauth://totp/", enrollBody.GetProperty("otpAuthUri").GetString());

        // A wrong code does not enable anything.
        var wrong = await client.PostAsJsonAsync("/2fa/enroll/confirm", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // The right code enables 2FA and issues recovery codes.
        var confirm = await client.PostAsJsonAsync("/2fa/enroll/confirm", new { code = CurrentCode(secret) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var recoveryCodes = (await confirm.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.True(recoveryCodes.Count >= 8);

        // Password alone now yields a challenge, not tokens.
        using var login = api.Client();
        var challenged = await ApiFixture.LoginAsync(login, email);
        Assert.True(challenged.GetProperty("twoFactorRequired").GetBoolean());
        var challengeToken = challenged.GetProperty("challengeToken").GetString();

        // The authenticator code completes the sign-in.
        var totpLogin = await login.PostAsJsonAsync("/auth/2fa", new { challengeToken, code = CurrentCode(secret) });
        Assert.Equal(HttpStatusCode.OK, totpLogin.StatusCode);
        var tokens = await totpLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("accessToken").GetString()));

        // A recovery code works too (lost-phone path) — once.
        var challenged2 = await ApiFixture.LoginAsync(login, email);
        var challengeToken2 = challenged2.GetProperty("challengeToken").GetString();
        var recovery = await login.PostAsJsonAsync("/auth/2fa/recovery",
            new { challengeToken = challengeToken2, recoveryCode = recoveryCodes[0] });
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);

        var challenged3 = await ApiFixture.LoginAsync(login, email);
        var reuse = await login.PostAsJsonAsync("/auth/2fa/recovery",
            new { challengeToken = challenged3.GetProperty("challengeToken").GetString(), recoveryCode = recoveryCodes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // Disable (with a fresh token from the TOTP login) returns login to single-factor.
        using var settled = api.Client();
        settled.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await settled.PostAsync("/2fa/disable", null)).StatusCode);

        var plain = await ApiFixture.LoginAsync(settled, email);
        Assert.False(string.IsNullOrEmpty(plain.GetProperty("accessToken").GetString()));
    }

    /// <summary>Reads the 'sub' claim out of a JWT without validating it — it is our own token.</summary>
    private static string ReadSubClaim(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=')
            .Replace('-', '+').Replace('_', '/');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(padded));
        return doc.RootElement.GetProperty("sub").GetString()!;
    }
}
