using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure.Migrations;
using WidgetWorks.Infrastructure.Persistence;
using WidgetWorks.Infrastructure.Seeding;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// Startup behaviour: migrations and the demo seed. Both run on every boot, so the property that
/// matters is idempotence — a second start must not duplicate an account, reset a password someone
/// changed, or re-add a widget an administrator deleted.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SeederAndMigrationTests(PostgresFixture db)
{
    private UserRepository Users => new(db.Connections);

    private static SeedOptions Options(string suffix) => new()
    {
        DemoAdminEmail = $"admin-{suffix}@widgetworks.test",
        DemoAdminPassword = "DemoAdmin!Change01",
        DemoCustomerEmail = $"demo-{suffix}@widgetworks.test",
        DemoCustomerPassword = "DemoUser!Change01",
        DemoManagerEmail = $"manager-{suffix}@widgetworks.test",
        DemoManagerPassword = "DemoManager!Change01",
    };

    private DbSeeder Seeder => new(db.Connections, new PlainHasher(), TimeProvider.System);

    [Fact]
    public async Task Seeding_creates_all_three_roles()
    {
        var options = Options(Guid.NewGuid().ToString("N")[..8]);

        await Seeder.SeedAsync(options, CancellationToken.None);

        var admin = await Users.GetByNormalizedEmailAsync(options.DemoAdminEmail.ToUpperInvariant(), CancellationToken.None);
        var manager = await Users.GetByNormalizedEmailAsync(options.DemoManagerEmail.ToUpperInvariant(), CancellationToken.None);
        var customer = await Users.GetByNormalizedEmailAsync(options.DemoCustomerEmail.ToUpperInvariant(), CancellationToken.None);

        Assert.Equal(UserRoles.Administrator, admin!.Role);
        Assert.Equal(UserRoles.Manager, manager!.Role);
        Assert.Equal(UserRoles.Customer, customer!.Role);
    }

    [Fact]
    public async Task Only_the_seeded_administrator_is_protected()
    {
        var options = Options(Guid.NewGuid().ToString("N")[..8]);

        await Seeder.SeedAsync(options, CancellationToken.None);

        Assert.True((await Users.GetByNormalizedEmailAsync(options.DemoAdminEmail.ToUpperInvariant(), CancellationToken.None))!.IsProtectedAdmin);
        Assert.False((await Users.GetByNormalizedEmailAsync(options.DemoManagerEmail.ToUpperInvariant(), CancellationToken.None))!.IsProtectedAdmin);
        Assert.False((await Users.GetByNormalizedEmailAsync(options.DemoCustomerEmail.ToUpperInvariant(), CancellationToken.None))!.IsProtectedAdmin);
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_an_account()
    {
        var options = Options(Guid.NewGuid().ToString("N")[..8]);

        await Seeder.SeedAsync(options, CancellationToken.None);
        var first = await Users.GetByNormalizedEmailAsync(options.DemoAdminEmail.ToUpperInvariant(), CancellationToken.None);

        await Seeder.SeedAsync(options, CancellationToken.None);
        var second = await Users.GetByNormalizedEmailAsync(options.DemoAdminEmail.ToUpperInvariant(), CancellationToken.None);

        // Same row, not a second one — the unique index would have thrown on a blind insert.
        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task Seeding_leaves_an_existing_password_alone()
    {
        var options = Options(Guid.NewGuid().ToString("N")[..8]);
        await Seeder.SeedAsync(options, CancellationToken.None);

        var user = await Users.GetByNormalizedEmailAsync(options.DemoCustomerEmail.ToUpperInvariant(), CancellationToken.None);
        user!.PasswordHash = "plain:changed-by-the-user";
        await Users.UpdateAsync(user, CancellationToken.None);

        await Seeder.SeedAsync(options, CancellationToken.None);

        // Restarting the app must never reset a password someone chose.
        var after = await Users.GetByNormalizedEmailAsync(options.DemoCustomerEmail.ToUpperInvariant(), CancellationToken.None);
        Assert.Equal("plain:changed-by-the-user", after!.PasswordHash);
    }

    [Fact]
    public async Task An_account_with_no_configured_password_is_skipped_rather_than_created_open()
    {
        var options = Options(Guid.NewGuid().ToString("N")[..8]);
        options.DemoManagerPassword = string.Empty;

        await Seeder.SeedAsync(options, CancellationToken.None);

        Assert.Null(await Users.GetByNormalizedEmailAsync(options.DemoManagerEmail.ToUpperInvariant(), CancellationToken.None));
    }

    [Fact]
    public async Task Seeding_stocks_the_demo_catalog_and_repeats_safely()
    {
        var widgets = new WidgetRepository(db.Connections);
        await Seeder.SeedAsync(Options(Guid.NewGuid().ToString("N")[..8]), CancellationToken.None);

        var standard = await widgets.GetBySkuAsync("WW-001", CancellationToken.None);
        Assert.Equal("Standard Widget Block Cobalt", standard!.Name);
        Assert.Equal(9.99m, standard.Price);

        await Seeder.SeedAsync(Options(Guid.NewGuid().ToString("N")[..8]), CancellationToken.None);

        // Still one row per SKU after a second run. The search has to name a whole
        // product including its finish: each department seeds five shapes in three
        // finishes, so any shorter term matches several of them.
        Assert.Equal(1, await widgets.CountAsync(new WidgetQuery("Standard Widget Block Cobalt", true, 1, 50), CancellationToken.None));
    }

    [Fact]
    public void Migrations_are_journaled_so_a_second_run_is_a_no_op()
    {
        // The fixture already migrated this database; running again must succeed without
        // reapplying anything, which is what makes restart-on-crash safe.
        MigrationRunner.Run(db.ConnectionString);

        var outcome = MigrationRunner.TryRun(db.ConnectionString);
        Assert.True(outcome.Successful);
    }

    [Fact]
    public void An_unreachable_database_is_reported_rather_than_thrown_at_startup()
    {
        var unreachable = "Host=localhost;Port=59999;Database=nope;Username=nobody;Password=nobody;Timeout=1";

        var outcome = MigrationRunner.TryRun(unreachable, maxAttempts: 2, firstDelay: TimeSpan.FromMilliseconds(1));

        // The behaviour that stops a free-tier container restart-looping and burning quota.
        Assert.False(outcome.Successful);
        Assert.NotNull(outcome.Error);
        Assert.Equal(2, outcome.Attempts);
    }

    [Fact]
    public async Task A_failing_script_is_reported_rather_than_thrown()
    {
        // A reachable server whose first migration cannot apply: DbUp reports the script error in
        // its result instead of throwing, and TryRun must surface that the same way it surfaces a
        // connection failure. Poisoning `users` with a view survives `create table if not exists`
        // (views share the relation namespace) but fails the index creation that follows.
        var poisoned = "ww_poison_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = "postgres" }.ConnectionString;
        await using var connection = new Npgsql.NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using (var create = new Npgsql.NpgsqlCommand($"create database \"{poisoned}\"", connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        var poisonedCs = new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = poisoned }.ConnectionString;
        try
        {
            await using (var target = new Npgsql.NpgsqlConnection(poisonedCs))
            {
                await target.OpenAsync();
                await using var poison = new Npgsql.NpgsqlCommand("create view users as select 'x' as normalized_email", target);
                await poison.ExecuteNonQueryAsync();
            }

            var outcome = MigrationRunner.TryRun(poisonedCs, maxAttempts: 1);

            Assert.False(outcome.Successful);
            Assert.NotNull(outcome.Error);
            Assert.Equal(1, outcome.Attempts);
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var drop = new Npgsql.NpgsqlCommand($"drop database if exists \"{poisoned}\" with (force)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Realignment_migration_corrects_a_row_the_seeder_will_not_touch()
    {
        var widgets = new WidgetRepository(db.Connections);
        await Seeder.SeedAsync(Options(Guid.NewGuid().ToString("N")[..8]), CancellationToken.None);

        // Rewind WW-002 to what a database seeded before the rename still holds. The SKU was
        // reassigned to a different product, so name, description and price are all wrong --
        // and the seeder will never correct it, because the SKU exists.
        using (var conn = await db.Connections.OpenAsync(CancellationToken.None))
        {
            await conn.ExecuteAsync(
                @"update widgets
                     set name = 'Deluxe Widget',
                         description = 'Premium finish with a reinforced housing.',
                         price = 24.99
                   where upper(sku) = 'WW-002'");
        }

        await Seeder.SeedAsync(Options(Guid.NewGuid().ToString("N")[..8]), CancellationToken.None);
        var stillStale = await widgets.GetBySkuAsync("WW-002", CancellationToken.None);
        Assert.Equal("Deluxe Widget", stillStale!.Name);

        using (var conn = await db.Connections.OpenAsync(CancellationToken.None))
        {
            await conn.ExecuteAsync(RealignmentScript());
        }

        var fixedRow = await widgets.GetBySkuAsync("WW-002", CancellationToken.None);
        Assert.Equal("Standard Widget Rotary Cobalt", fixedRow!.Name);
        Assert.Equal("Dial-adjustable everyday widget.", fixedRow.Description);
        Assert.Equal(11.49m, fixedRow.Price);
    }

    [Fact]
    public async Task Realignment_migration_leaves_stock_alone_and_repeats_safely()
    {
        var widgets = new WidgetRepository(db.Connections);
        await Seeder.SeedAsync(Options(Guid.NewGuid().ToString("N")[..8]), CancellationToken.None);

        // Stock is operational -- it moves with orders and reservations, and forcing it back to a
        // seed figure could put quantity_on_hand below quantity_reserved and fail the check
        // constraint. The realignment corrects catalogue text and price only.
        using (var conn = await db.Connections.OpenAsync(CancellationToken.None))
        {
            await conn.ExecuteAsync("update widgets set quantity_on_hand = 7 where upper(sku) = 'WW-016'");
            await conn.ExecuteAsync(RealignmentScript());
            await conn.ExecuteAsync(RealignmentScript());
        }

        var widget = await widgets.GetBySkuAsync("WW-016", CancellationToken.None);
        Assert.Equal("Mini Widget Block Jade", widget!.Name);
        Assert.Equal(7, widget.QuantityOnHand);
    }

    /// <summary>
    /// The realignment SQL, read from the same embedded resource DbUp deploys, so the test
    /// exercises the script that actually ships rather than a copy of it.
    /// </summary>
    private static string RealignmentScript()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("0012_RealignDemoCatalog.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Deterministic stand-in: the seeder only needs *a* hasher, not a slow one.</summary>
    private sealed class PlainHasher : IPasswordHasher
    {
        public string Hash(string password) => "plain:" + password;

        public bool Verify(string password, string hash) => hash == "plain:" + password;
    }
}
