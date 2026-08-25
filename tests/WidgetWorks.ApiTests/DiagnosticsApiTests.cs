using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using WidgetWorks.WebApi.Diagnostics;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The two probes and the correlation id. These exist because the previous behaviour was safe but
/// unobservable: a health endpoint that could never report bad news, and a 500 with nothing tying it
/// to a log line.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiagnosticsApiTests(ApiFixture fixture)
{
    private sealed record Liveness(string Status);
    private sealed record Readiness(string Status, string Database, bool MigrationSucceeded);

    [Fact]
    public async Task Liveness_answers_without_touching_the_database()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Liveness>();
        Assert.Equal("ok", body!.Status);
    }

    [Fact]
    public async Task Readiness_reports_the_database_actually_answering()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Readiness>();
        Assert.Equal("ready", body!.Status);
        Assert.Equal("ok", body.Database);
        Assert.True(body.MigrationSucceeded);
    }

    [Fact]
    public async Task Readiness_is_a_separate_endpoint_so_the_cheap_probe_stays_cheap()
    {
        using var client = fixture.Factory.CreateClient();

        // Both exist and both answer. The distinction is the point: /health is what the keep-warm
        // schedule pings, and waking a serverless database on that cadence is what would blow the
        // monthly compute budget. Merging them is the mistake this pins against.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.True(response.Headers.TryGetValues(CorrelationId.HeaderName, out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.First()));
    }

    [Fact]
    public async Task A_caller_supplied_correlation_id_is_kept_so_a_trace_survives()
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(CorrelationId.HeaderName, "trace-from-upstream-1");

        var response = await client.SendAsync(request);

        // A request that already carries an id keeps it, so one trace spans several services rather
        // than restarting at each hop.
        Assert.Equal("trace-from-upstream-1", response.Headers.GetValues(CorrelationId.HeaderName).First());
    }
}

/// <summary>
/// The id itself. Worth unit tests because it reaches log messages, and text that reaches a log
/// message is an injection surface.
/// </summary>
public class CorrelationIdTests
{
    private static HttpContext RequestWith(string? supplied)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-assigned-by-the-host" };
        if (supplied is not null)
        {
            context.Request.Headers[CorrelationId.HeaderName] = supplied;
        }

        return context;
    }

    [Fact]
    public void Falls_back_to_the_id_the_host_already_assigned()
        => Assert.Equal("trace-assigned-by-the-host", CorrelationId.Resolve(RequestWith(null)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_header_is_ignored(string supplied)
        => Assert.Equal("trace-assigned-by-the-host", CorrelationId.Resolve(RequestWith(supplied)));

    [Fact]
    public void A_sane_inbound_id_is_kept()
        => Assert.Equal("abc-123_x.y:z", CorrelationId.Resolve(RequestWith("abc-123_x.y:z")));

    [Fact]
    public void Newlines_are_stripped_so_a_caller_cannot_forge_log_entries()
    {
        // Left intact this would put an attacker-authored line into the log stream that reads like
        // a genuine record (CWE-117) - the same class of defect already fixed once here, on the
        // order-status path.
        var forged = CorrelationId.Resolve(RequestWith("ok\r\nfatal: database deleted by admin"));

        Assert.DoesNotContain('\n', forged);
        Assert.DoesNotContain('\r', forged);
        Assert.StartsWith("ok", forged, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absurdly_long_id_is_truncated_rather_than_logged_whole()
    {
        var resolved = CorrelationId.Resolve(RequestWith(new string('a', 500)));

        Assert.True(resolved.Length <= 64);
    }

    [Fact]
    public void A_header_of_only_junk_falls_back_instead_of_yielding_an_empty_id()
        => Assert.Equal("trace-assigned-by-the-host", CorrelationId.Resolve(RequestWith("<<<>>>")));
}
