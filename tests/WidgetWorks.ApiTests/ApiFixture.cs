using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The whole API in process, over a throwaway PostgreSQL database. The unit suite proves the
/// handlers and the integration suite proves the SQL; this suite proves the part neither can --
/// the HTTP surface itself: routing, model binding, the JWT bearer pipeline with its
/// security-stamp check, and the authorization policies that separate Customer, Manager and
/// Administrator.
///
/// The factory boots the real Program (migrations + seeding included) against a database created
/// for the run and dropped afterwards, exactly like PostgresFixture does for the repositories.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private const string DefaultAdmin =
        "Host=localhost;Port=5432;Database=postgres;Username=widgetworks;Password=replace-me-locally";

    // 'test-signing-key' prefix keeps this throwaway value out of the gitleaks gate.
    private const string SigningKey = "test-signing-key-api-suite-0123456789abcdef";

    private string _adminConnectionString = DefaultAdmin;

    public const string AdminEmail = "api-admin@widgetworks.test";
    public const string ManagerEmail = "api-manager@widgetworks.test";
    public const string CustomerEmail = "api-customer@widgetworks.test";
    public const string Password = "ApiSuite!Change01";

    public string DatabaseName { get; } = "ww_api_" + Guid.NewGuid().ToString("N")[..12];

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("WIDGETWORKS_TEST_DB") ?? DefaultAdmin;

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = "postgres" };
        await using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"create database \"{DatabaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = DatabaseName }
            .ConnectionString;

        // UseSetting lands in host configuration, which minimal hosting folds into
        // builder.Configuration BEFORE Program's own code reads it; ConfigureAppConfiguration
        // callbacks would run too late for the Jwt options Program binds during startup.
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(Environments.Production);   // no OpenAPI/Scalar noise in tests
            host.UseSetting("ConnectionStrings:WidgetWorks", connectionString);
            host.UseSetting("Jwt:SigningKey", SigningKey);
            host.UseSetting("Seed:DemoAdminEmail", AdminEmail);
            host.UseSetting("Seed:DemoAdminPassword", Password);
            host.UseSetting("Seed:DemoManagerEmail", ManagerEmail);
            host.UseSetting("Seed:DemoManagerPassword", Password);
            host.UseSetting("Seed:DemoCustomerEmail", CustomerEmail);
            host.UseSetting("Seed:DemoCustomerPassword", Password);
        });

        // First client boots the host: migrations run and the demo catalog is seeded.
        using var client = Factory.CreateClient();
        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists \"{DatabaseName}\" with (force)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public HttpClient Client() => Factory.CreateClient();

    /// <summary>Signs in and returns a client that sends the bearer token on every request.</summary>
    public async Task<HttpClient> SignedInAsync(string email, string password = Password)
    {
        var client = Client();
        var tokens = await LoginAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    public static async Task<JsonElement> LoginAsync(HttpClient client, string email, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Registers a fresh customer and returns a signed-in client plus the email.</summary>
    public async Task<(HttpClient Client, string Email)> FreshCustomerAsync()
    {
        var email = $"c-{Guid.NewGuid():N}@widgetworks.test";
        var client = Client();
        var register = await client.PostAsJsonAsync("/auth/register", new { email, password = Password });
        register.EnsureSuccessStatusCode();
        var tokens = await LoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, email);
    }
}

/// <summary>One database and one host shared by every API suite; tests create their own data.</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
