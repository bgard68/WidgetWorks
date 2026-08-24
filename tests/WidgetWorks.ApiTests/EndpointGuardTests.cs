using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The endpoints' own sub-claim guards. Through the real JWT pipeline these lines are
/// unreachable -- the bearer events already reject a token whose sub is not a user id -- so this
/// suite swaps in a permissive test scheme that will authenticate any principal, and proves the
/// endpoints still refuse one whose sub does not parse. Defense in depth stays defended: adding
/// a second authentication scheme later cannot silently hand these routes a nonsense identity.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointGuardTests(ApiFixture api) : IDisposable
{
    private HttpClient? _client;

    private HttpClient Client()
    {
        // A derived factory over the same database; the test scheme becomes the default.
        var factory = api.Factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddAuthentication(GuardScheme.Name)
                .AddScheme<AuthenticationSchemeOptions, GuardScheme>(GuardScheme.Name, _ => { })));
        _client = factory.CreateClient();
        return _client;
    }

    public void Dispose() => _client?.Dispose();

    [Theory]
    [InlineData("GET", "/orders")]
    [InlineData("GET", "/orders/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/auth/secure-account")]
    [InlineData("POST", "/2fa/enroll")]
    [InlineData("POST", "/2fa/enroll/confirm")]
    [InlineData("POST", "/2fa/disable")]
    [InlineData("POST", "/cart/merge")]
    public async Task An_authenticated_principal_without_a_user_id_is_still_refused(string method, string path)
    {
        var client = Client();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = path switch
            {
                "/2fa/enroll/confirm" => JsonContent.Create(new { code = "000000" }),
                "/cart/merge" => JsonContent.Create(new { guestCartId = Guid.NewGuid() }),
                _ => JsonContent.Create(new { }),
            };
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Authenticates everything, with a sub claim no user id could ever have.</summary>
    private sealed class GuardScheme(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Guard";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "not-a-user-id")], Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
