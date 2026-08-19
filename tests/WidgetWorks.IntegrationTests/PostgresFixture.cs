using Npgsql;
using WidgetWorks.Infrastructure.Migrations;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// A real PostgreSQL database for the repository suites. The Dapper repositories are mostly SQL --
/// the atomic stock reservation, the ON CONFLICT upserts, the partial unique index on live widget
/// names -- and none of that can be exercised by an in-memory fake. A fake would only prove the
/// fake works.
///
/// It connects to the Postgres that `docker compose up db` already provides (override with
/// WIDGETWORKS_TEST_DB), then creates a **throwaway database per run** and migrates it, so the
/// suite never touches developer or demo data and parallel runs cannot collide.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DefaultAdmin =
        "Host=localhost;Port=5432;Database=postgres;Username=widgetworks;Password=replace-me-locally";

    private string _adminConnectionString = DefaultAdmin;

    public string DatabaseName { get; } = "ww_test_" + Guid.NewGuid().ToString("N")[..12];

    public string ConnectionString { get; private set; } = string.Empty;

    public IDbConnectionFactory Connections { get; private set; } = null!;

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

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = DatabaseName }
            .ConnectionString;

        // The same Dapper mapping and the same DbUp migrations the application runs at startup, so
        // the schema and the mapping under test are the ones that ship.
        DapperConfiguration.Apply();
        MigrationRunner.Run(ConnectionString);

        Connections = new NpgsqlConnectionFactory(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists \"{DatabaseName}\" with (force)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>One database shared by every repository suite; each test cleans up after itself.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
