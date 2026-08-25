using System.Net;
using Microsoft.AspNetCore.Http;
using WidgetWorks.WebApi.RateLimiting;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// Which caller a request is attributed to. This is the whole correctness of throttling: get the
/// partition key wrong and the limiter either caps everybody together or caps nobody at all.
/// </summary>
public class ClientAddressTests
{
    private static HttpContext Request(string? remoteIp, params string[] forwardedFor)
    {
        var context = new DefaultHttpContext();
        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        if (forwardedFor.Length > 0)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    [Fact]
    public void The_connection_address_identifies_the_caller_by_default()
        => Assert.Equal("203.0.113.7", ClientAddress.Resolve(Request("203.0.113.7"), trustForwardedFor: false));

    [Fact]
    public void A_forwarded_header_is_ignored_unless_a_proxy_is_trusted()
    {
        // Anyone can send this header. Believing it without a proxy in front would let a caller
        // mint a fresh partition per request and opt out of throttling entirely.
        var context = Request("203.0.113.7", "198.51.100.9");

        Assert.Equal("203.0.113.7", ClientAddress.Resolve(context, trustForwardedFor: false));
    }

    [Fact]
    public void A_trusted_proxy_reveals_the_original_client()
    {
        var context = Request("10.0.0.1", "198.51.100.9");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Fact]
    public void The_leftmost_entry_in_a_forwarded_chain_is_the_client()
    {
        // Proxies append, so the client is first and every hop after it is infrastructure.
        var context = Request("10.0.0.1", "198.51.100.9, 10.0.0.8, 10.0.0.1");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Fact]
    public void A_chain_split_across_repeated_headers_is_read_the_same_way()
    {
        var context = Request("10.0.0.1", "198.51.100.9", "10.0.0.8");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void An_empty_forwarded_header_falls_back_to_the_connection(string headerValue)
    {
        var context = Request("203.0.113.7", headerValue);

        Assert.Equal("203.0.113.7", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Fact]
    public void Callers_with_no_determinable_address_share_one_budget()
    {
        // Failing closed: unattributable traffic is throttled together rather than exempted.
        Assert.Equal(ClientAddress.Unknown, ClientAddress.Resolve(Request(null), trustForwardedFor: false));
        Assert.Equal(ClientAddress.Unknown, ClientAddress.Resolve(Request(null), trustForwardedFor: true));
    }

    [Fact]
    public void A_null_context_is_a_programming_error_not_a_silent_pass()
        => Assert.Throws<ArgumentNullException>(() => ClientAddress.Resolve(null!, trustForwardedFor: false));
}
