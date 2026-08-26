using System.Net;
using Microsoft.AspNetCore.Http;
using WidgetWorks.WebApi.RateLimiting;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// Which caller a request is attributed to. This is the whole correctness of throttling: get the
/// partition key wrong and the limiter either caps everybody together or caps nobody at all.
///
/// An earlier version of these tests asserted that the leftmost entry of an <c>X-Forwarded-For</c>
/// chain is the client. That is the natural reading and it is wrong, because a proxy appends to the
/// header rather than replacing it — so position zero is whatever the caller chose to send. The test
/// passed, described the code accurately, and pinned the defect in place.
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
        // One hop, nothing forged: the proxy appended the client and that is the only entry.
        var context = Request("10.0.0.1", "198.51.100.9");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Fact]
    public void A_caller_cannot_choose_its_own_partition_by_sending_a_forwarded_header()
    {
        // The regression. A caller sends "9.9.9.9"; the proxy appends the address it actually saw,
        // so the real client is last and the forged value sits in front of it. Reading position zero
        // would return the attacker's choice, and varying it per request would mint a fresh partition
        // every time — throttling defeated while TrustForwardedFor is correctly true.
        var context = Request("10.0.0.1", "9.9.9.9, 198.51.100.9");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Fact]
    public void A_forged_prefix_of_any_length_is_ignored()
    {
        // Padding the chain does not move the trusted entry: it is found by counting from the right.
        var context = Request("10.0.0.1", "1.1.1.1, 2.2.2.2, 3.3.3.3, 4.4.4.4, 198.51.100.9");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Fact]
    public void Two_callers_forging_different_headers_still_share_one_partition()
    {
        // The property that matters for throttling, stated directly: the key depends on where the
        // request came from, not on what it claimed.
        var first = ClientAddress.Resolve(Request("10.0.0.1", "1.1.1.1, 198.51.100.9"), trustForwardedFor: true);
        var second = ClientAddress.Resolve(Request("10.0.0.1", "2.2.2.2, 198.51.100.9"), trustForwardedFor: true);

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_port_App_Service_appends_is_not_part_of_the_caller_identity()
    {
        // App Service writes the client as ip:port, and the source port is ephemeral. Keeping it
        // would partition per connection rather than per caller — the same escape by another route.
        var first = ClientAddress.Resolve(Request("10.0.0.1", "198.51.100.9:51514"), trustForwardedFor: true);
        var second = ClientAddress.Resolve(Request("10.0.0.1", "198.51.100.9:60122"), trustForwardedFor: true);

        Assert.Equal("198.51.100.9", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void An_IPv6_client_survives_with_and_without_a_port()
    {
        // A bare IPv6 address is full of colons, so it must not be mistaken for a host:port pair.
        Assert.Equal(
            "2001:db8::1",
            ClientAddress.Resolve(Request("10.0.0.1", "2001:db8::1"), trustForwardedFor: true));

        Assert.Equal(
            "2001:db8::1",
            ClientAddress.Resolve(Request("10.0.0.1", "[2001:db8::1]:51514"), trustForwardedFor: true));
    }

    [Fact]
    public void A_chain_split_across_repeated_headers_is_read_the_same_way()
    {
        // Two headers, one logical chain: the client is still the final entry overall.
        var context = Request("10.0.0.1", "9.9.9.9", "198.51.100.9");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true));
    }

    [Fact]
    public void A_second_trusted_hop_moves_the_client_one_entry_further_left()
    {
        // With a CDN in front of App Service both append, so the client sits two from the end.
        var context = Request("10.0.0.1", "9.9.9.9, 198.51.100.9, 10.0.0.8");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true, trustedProxyHops: 2));
    }

    [Fact]
    public void A_hop_count_longer_than_the_chain_falls_back_instead_of_guessing()
    {
        // Misconfigured: this throttles everyone as one caller, which ProxyConfigurationCheck warns
        // about. Falling back to the connection address is the safe half of a bad situation — the
        // alternative is reading an entry the caller supplied.
        var context = Request("10.0.0.1", "198.51.100.9");

        Assert.Equal("10.0.0.1", ClientAddress.Resolve(context, trustForwardedFor: true, trustedProxyHops: 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonsensical_hop_count_is_treated_as_one_proxy(int hops)
    {
        // Zero would index past the end of every chain and quietly collapse all callers together.
        var context = Request("10.0.0.1", "9.9.9.9, 198.51.100.9");

        Assert.Equal("198.51.100.9", ClientAddress.Resolve(context, trustForwardedFor: true, trustedProxyHops: hops));
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

    [Theory]
    [InlineData("unknown")]
    [InlineData("_hidden")]
    [InlineData("not-an-address")]
    public void An_entry_that_is_not_an_address_is_not_used_as_an_identity(string entry)
    {
        // The standard permits these, and none of them names a caller. Used as a key, every caller
        // sending the same placeholder would share one budget under a name that reads specific.
        var context = Request("203.0.113.7", entry);

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
