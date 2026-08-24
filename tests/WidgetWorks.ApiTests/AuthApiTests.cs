using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WidgetWorks.ApiTests;

[Collection(ApiCollection.Name)]
public class AuthApiTests(ApiFixture api)
{
    [Fact]
    public async Task Register_then_login_yields_a_working_bearer_token()
    {
        var email = $"c-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();

        var register = await client.PostAsJsonAsync("/auth/register", new { email, password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var tokens = await ApiFixture.LoginAsync(client, email);
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refreshToken").GetString()));
        Assert.Equal("Customer", tokens.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Register_with_a_short_password_is_a_400_with_a_reason()
    {
        using var client = api.Client();

        var response = await client.PostAsJsonAsync("/auth/register",
            new { email = $"c-{Guid.NewGuid():N}@widgetworks.test", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("8 characters", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_wrong_password_is_a_401()
    {
        using var client = api.Client();

        var response = await client.PostAsJsonAsync("/auth/login",
            new { email = ApiFixture.CustomerEmail, password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_session_and_logout_ends_it()
    {
        var email = $"c-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        (await client.PostAsJsonAsync("/auth/register", new { email, password = ApiFixture.Password })).EnsureSuccessStatusCode();
        var tokens = await ApiFixture.LoginAsync(client, email);
        var refreshToken = tokens.GetProperty("refreshToken").GetString();

        var refreshed = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var next = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var rotated = next.GetProperty("refreshToken").GetString();
        Assert.NotEqual(refreshToken, rotated);

        var logout = await client.PostAsJsonAsync("/auth/logout", new { refreshToken = rotated });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The logged-out token no longer refreshes.
        var reuse = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = rotated });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Refresh_of_garbage_is_a_401()
    {
        using var client = api.Client();

        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = "nonsense" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_always_answers_200()
    {
        using var client = api.Client();

        var known = await client.PostAsJsonAsync("/auth/forgot-password", new { email = ApiFixture.CustomerEmail });
        var unknown = await client.PostAsJsonAsync("/auth/forgot-password", new { email = "nobody@widgetworks.test" });

        // Identical responses: the endpoint must not be an account-existence oracle.
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
    }

    [Fact]
    public async Task Reset_password_with_a_bogus_token_is_a_400()
    {
        using var client = api.Client();

        var response = await client.PostAsJsonAsync("/auth/reset-password",
            new { token = "bogus", newPassword = "long-enough-pw" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Google_login_with_an_invalid_token_is_a_401()
    {
        using var client = api.Client();

        // No Google client id is configured for the suite, so every token is refused.
        var response = await client.PostAsJsonAsync("/auth/google", new { idToken = "not-a-google-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Two_factor_login_with_a_garbage_challenge_is_a_401()
    {
        using var client = api.Client();

        var totp = await client.PostAsJsonAsync("/auth/2fa", new { challengeToken = "garbage", code = "000000" });
        var recovery = await client.PostAsJsonAsync("/auth/2fa/recovery", new { challengeToken = "garbage", recoveryCode = "nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, totp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, recovery.StatusCode);
    }

    [Fact]
    public async Task A_protected_endpoint_refuses_anonymous_and_tampered_tokens()
    {
        using var anonymous = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/orders")).StatusCode);

        using var tampered = api.Client();
        tampered.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not.a.jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await tampered.GetAsync("/orders")).StatusCode);
    }
}
