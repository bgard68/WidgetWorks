using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using WidgetWorks.WebApi.Diagnostics;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// What the API says when its database is gone.
///
/// This is the one failure the app is built to survive rather than exit on — startup deliberately
/// does not throw, so the process stays up and the probes carry the bad news instead. That makes the
/// outage responses a real contract: a platform probe decides whether to route traffic here from
/// them, and an operator diagnoses from them.
///
/// It is also the moment a service is most likely to leak. The connection failure underneath these
/// responses knows the host, the database name and the username, and every endpoint below is
/// anonymous — so each test asserts both what the caller is told and what they are not.
/// </summary>
[Collection(OfflineApiCollection.Name)]
public sealed class DatabaseOutageApiTests(OfflineApiFixture offline)
{
    private HttpClient Client => offline.Client;

    [Fact]
    public async Task Liveness_MigrationFailedAtStartup_Reports503WithoutTheConnectionString()
    {
        // Act — the probe the provisioning script watches on first boot.
        var response = await Client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        // Assert — the process is up, so it can answer; the answer is that it is not healthy.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("unhealthy", json.GetProperty("status").GetString());
        Assert.Equal("database migration failed", json.GetProperty("reason").GetString());

        // The detail field exists to make this diagnosable, which is exactly why it must not carry
        // the credentials out to an anonymous caller.
        Assert.DoesNotContain("outage_probe_user", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readiness_DatabaseUnreachable_Reports503WithTheExceptionTypeAndNothingElse()
    {
        // Act — the probe the platform routes traffic on.
        var response = await Client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        // Assert — a 503 is what stops traffic arriving at an instance that cannot serve it.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("not ready", json.GetProperty("status").GetString());
        Assert.Equal("unreachable", json.GetProperty("database").GetString());

        // The type name is enough to tell a refused connection from a timeout or an auth failure.
        // The message is not, because it names the host and the user.
        Assert.EndsWith("Exception", json.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("outage_probe_user", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ww_outage_probe", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledException_AnonymousCatalogRequest_Returns500WithAMatchingCorrelationIdAndNoDetail()
    {
        // Act — the public storefront listing, which cannot answer without the database.
        var response = await Client.GetAsync("/catalog/widgets");
        var body = await response.Content.ReadAsStringAsync();

        // Assert — a generic 500 whose only specific content is the reference the caller can quote.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(
            "Something went wrong on our side. Quote the reference below if you contact us.",
            json.GetProperty("error").GetString());

        // The id in the body and the id on the header have to be the same value, or a customer
        // quoting one of them cannot be matched to the log line carrying the other.
        var header = Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName));
        Assert.Equal(header, json.GetProperty("correlationId").GetString());
        Assert.NotEmpty(header);

        // An exception message here would name the host, the database and the driver.
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outage_probe_user", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnhandledException_CallerSuppliesACorrelationId_EchoesTheirIdBackOnTheFailure()
    {
        // Arrange — a caller correlating a request across their own logs and ours.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/catalog/widgets");
        request.Headers.Add(CorrelationId.HeaderName, "outage-trace-42");

        // Act
        var response = await Client.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert — the failure path keeps their id rather than minting a new one, so the id they
        // already logged is the id our log line carries.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("outage-trace-42", json.GetProperty("correlationId").GetString());
        Assert.Equal("outage-trace-42", Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName)));
    }
}

/// <summary>
/// The API booted against a database that is not there.
///
/// Its own fixture rather than a variant of <see cref="ApiFixture"/> because startup retries the
/// migration with exponential backoff before conceding, which costs roughly fifteen seconds. xUnit
/// builds a fresh test-class instance per test, so paying that per test would have made one small
/// suite the slowest thing in the run; a collection fixture pays it once and xUnit disposes it.
/// </summary>
public sealed class OfflineApiFixture : IAsyncLifetime
{
    /// <summary>
    /// Port 1 refuses immediately rather than hanging, so the outage is instant. The host, database
    /// and username are deliberately distinctive: they are what the leak assertions search for.
    /// </summary>
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=ww_outage_probe;Username=outage_probe_user;Password=replace-me-locally;Timeout=1;Command Timeout=1";

    // Same 'test-signing-key' prefix ApiFixture uses, which keeps the throwaway value out of the
    // gitleaks gate. Startup binds JwtOptions, so the host will not boot without one.
    private const string SigningKey = "test-signing-key-outage-suite-0123456789abcdef";

    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(Environments.Production);
            host.UseSetting("ConnectionStrings:WidgetWorks", UnreachableDatabase);
            host.UseSetting("Jwt:SigningKey", SigningKey);
        });

        Client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class OfflineApiCollection : ICollectionFixture<OfflineApiFixture>
{
    public const string Name = "offline-api";
}
