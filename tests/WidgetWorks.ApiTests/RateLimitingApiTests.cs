using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// Proves the throttling actually engages. The shared fixture raises every budget so the suite can
/// run, which would otherwise leave this control shipped but unexercised — so these tests stand up
/// their own host with a budget of two and drive straight past it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitingApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task A_flood_of_sign_in_attempts_is_rejected_once_the_budget_is_spent()
    {
        using var factory = fixture.FactoryWith(("RateLimiting:Auth:PermitLimit", "2"));
        using var client = factory.CreateClient();

        var attempt = new { Email = "nobody@widgetworks.test", Password = "WrongPassword!1" };

        // Two are allowed. Whether they succeed is beside the point: a wrong password is still a
        // request, which is exactly why throttling has to sit in front of authentication.
        var first = await client.PostAsJsonAsync("/auth/login", attempt);
        var second = await client.PostAsJsonAsync("/auth/login", attempt);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);

        var third = await client.PostAsJsonAsync("/auth/login", attempt);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task A_rejected_caller_is_told_how_long_to_wait()
    {
        using var factory = fixture.FactoryWith(("RateLimiting:Auth:PermitLimit", "1"));
        using var client = factory.CreateClient();

        var attempt = new { Email = "nobody@widgetworks.test", Password = "WrongPassword!1" };
        await client.PostAsJsonAsync("/auth/login", attempt);
        var rejected = await client.PostAsJsonAsync("/auth/login", attempt);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Retry-After turns a wall into a queue: a well-behaved client backs off instead of
        // hammering, and an honest caller who tripped the limit recovers without asking anyone.
        Assert.True(rejected.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.TryParse(retryAfter!.First(), out var seconds) && seconds > 0);

        var body = await rejected.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Error));
    }

    [Fact]
    public async Task Throttling_is_scoped_to_the_policy_not_the_whole_api()
    {
        using var factory = fixture.FactoryWith(("RateLimiting:Auth:PermitLimit", "1"));
        using var client = factory.CreateClient();

        var attempt = new { Email = "nobody@widgetworks.test", Password = "WrongPassword!1" };
        await client.PostAsJsonAsync("/auth/login", attempt);
        var rejected = await client.PostAsJsonAsync("/auth/login", attempt);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Browsing must keep working while the auth budget is spent. A global limiter would fail
        // this, which is the reason there isn't one.
        var browsing = await client.GetAsync("/catalog/widgets?pageSize=5");
        Assert.Equal(HttpStatusCode.OK, browsing.StatusCode);
    }

    private sealed record ErrorBody(string Error);
}
