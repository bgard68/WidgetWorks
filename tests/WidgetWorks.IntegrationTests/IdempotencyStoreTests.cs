using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// The idempotency ledger against real PostgreSQL.
///
/// This suite exists for one claim that cannot be tested anywhere else: the claim is atomic. In the
/// unit suite the store is a dictionary behind a lock, which proves the handler uses the contract
/// but not that the contract holds. Here the arbitration is an INSERT with <c>on conflict do
/// nothing</c> on a real server over real concurrent connections, which is the only place the
/// promise is actually kept.
/// </summary>
[Collection(PostgresCollection.Name)]
public class IdempotencyStoreTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private IdempotencyStore Store => new(db.Connections);

    private static string FreshScope() => "checkout:" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task One_claim_wins_and_the_rest_are_told_it_is_in_flight()
    {
        var store = Store;
        var scope = FreshScope();

        var claims = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.TryClaimAsync(scope, "same-key", "hash-a", Now, CancellationToken.None))));

        // The assertion the whole feature rests on.
        Assert.Equal(1, claims.Count(c => c.Claim == IdempotencyClaim.Claimed));
        Assert.Equal(31, claims.Count(c => c.Claim == IdempotencyClaim.InFlight));
    }

    [Fact]
    public async Task A_completed_key_replays_its_stored_response()
    {
        var store = Store;
        var scope = FreshScope();

        var claim = await store.TryClaimAsync(scope, "k", "hash-a", Now, CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Claimed, claim.Claim);

        await store.CompleteAsync(scope, "k", "{\"orderNumber\":\"WW-1\"}", null, Now, CancellationToken.None);

        var replay = await store.TryClaimAsync(scope, "k", "hash-a", Now, CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Replay, replay.Claim);
        Assert.Equal("{\"orderNumber\":\"WW-1\"}", replay.Response);
        Assert.Null(replay.Error);
    }

    [Fact]
    public async Task A_stored_failure_replays_as_a_failure()
    {
        var store = Store;
        var scope = FreshScope();

        await store.TryClaimAsync(scope, "k", "hash-a", Now, CancellationToken.None);
        await store.CompleteAsync(scope, "k", null, "Your card was declined.", Now, CancellationToken.None);

        var replay = await store.TryClaimAsync(scope, "k", "hash-a", Now, CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Replay, replay.Claim);
        Assert.Null(replay.Response);
        Assert.Equal("Your card was declined.", replay.Error);
    }

    [Fact]
    public async Task A_different_payload_under_the_same_key_is_a_mismatch_in_both_states()
    {
        var store = Store;
        var scope = FreshScope();

        await store.TryClaimAsync(scope, "k", "hash-a", Now, CancellationToken.None);

        // Still in flight.
        Assert.Equal(IdempotencyClaim.PayloadMismatch, (await store.TryClaimAsync(scope, "k", "hash-b", Now, CancellationToken.None)).Claim);

        await store.CompleteAsync(scope, "k", "{}", null, Now, CancellationToken.None);

        // And after completion — the mismatch check comes before any thought of replaying.
        Assert.Equal(IdempotencyClaim.PayloadMismatch, (await store.TryClaimAsync(scope, "k", "hash-b", Now, CancellationToken.None)).Claim);
    }

    [Fact]
    public async Task The_same_key_in_a_different_scope_is_a_different_key()
    {
        var store = Store;

        // Two shoppers who both happened to send "retry-1" for their own basket. Scoping by cart is
        // what keeps one of them from being handed the other's order.
        Assert.Equal(IdempotencyClaim.Claimed, (await store.TryClaimAsync(FreshScope(), "retry-1", "hash-a", Now, CancellationToken.None)).Claim);
        Assert.Equal(IdempotencyClaim.Claimed, (await store.TryClaimAsync(FreshScope(), "retry-1", "hash-a", Now, CancellationToken.None)).Claim);
    }

    [Fact]
    public async Task A_second_completion_cannot_overwrite_a_settled_answer()
    {
        var store = Store;
        var scope = FreshScope();

        await store.TryClaimAsync(scope, "k", "hash-a", Now, CancellationToken.None);
        await store.CompleteAsync(scope, "k", "{\"orderNumber\":\"WW-FIRST\"}", null, Now, CancellationToken.None);
        await store.CompleteAsync(scope, "k", "{\"orderNumber\":\"WW-SECOND\"}", null, Now, CancellationToken.None);

        var replay = await store.TryClaimAsync(scope, "k", "hash-a", Now, CancellationToken.None);
        Assert.Contains("WW-FIRST", replay.Response);
    }

    [Fact]
    public async Task Purging_removes_keys_past_retention_and_leaves_live_ones()
    {
        var store = Store;
        var scope = FreshScope();

        await store.TryClaimAsync(scope, "old", "hash-a", Now.AddHours(-48), CancellationToken.None);
        await store.TryClaimAsync(scope, "fresh", "hash-a", Now.AddMinutes(-5), CancellationToken.None);

        var purged = await store.PurgeExpiredAsync(Now.AddHours(-24), CancellationToken.None);
        Assert.True(purged >= 1);

        using var connection = await db.Connections.OpenAsync(CancellationToken.None);
        var surviving = await connection.QueryAsync<string>(
            "select key from idempotency_keys where scope = @Scope", new { Scope = scope });

        Assert.Equal("fresh", Assert.Single(surviving));
    }
}
