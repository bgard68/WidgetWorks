using Dapper;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Persistence;

/// <summary>
/// The idempotency ledger in PostgreSQL. The whole design rests on one line of SQL: an INSERT with
/// <c>on conflict do nothing</c> against the primary key. Two concurrent claims both run it, the
/// database serialises them, and exactly one reports a row inserted. That is the atomic
/// check-and-create the naive "read, then write if absent" version cannot provide.
/// </summary>
public sealed class IdempotencyStore(IDbConnectionFactory factory) : IIdempotencyStore
{
    private const string InProgress = "in_progress";
    private const string Completed = "completed";

    private const string ClaimSql =
        @"insert into idempotency_keys (scope, key, request_hash, status, created_at)
          values (@Scope, @Key, @RequestHash, @Status, @Now)
          on conflict (scope, key) do nothing";

    private const string ReadSql =
        @"select request_hash, status, response_body, response_error
          from idempotency_keys
          where scope = @Scope and key = @Key";

    public async Task<IdempotencyRecord> TryClaimAsync(string scope, string key, string requestHash, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);

        var inserted = await db.ExecuteAsync(new CommandDefinition(
            ClaimSql,
            new { Scope = scope, Key = key, RequestHash = requestHash, Status = InProgress, Now = now },
            cancellationToken: ct));

        if (inserted == 1)
        {
            return new IdempotencyRecord(IdempotencyClaim.Claimed, null, null);
        }

        var existing = await db.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition(
            ReadSql, new { Scope = scope, Key = key }, cancellationToken: ct));

        if (existing is null)
        {
            // The row lost the insert race and then vanished — only a retention purge between the
            // two statements can do that, and a key old enough to purge is not a live retry. Treat
            // it as a fresh request rather than inventing a conflict the caller cannot act on.
            return new IdempotencyRecord(IdempotencyClaim.Claimed, null, null);
        }

        // Checked before status: a key reused for different content is a client bug whichever state
        // the original is in, and answering with the first request's response would be worse than
        // refusing.
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IdempotencyRecord(IdempotencyClaim.PayloadMismatch, null, null);
        }

        return existing.Status == Completed
            ? new IdempotencyRecord(IdempotencyClaim.Replay, existing.ResponseBody, existing.ResponseError)
            : new IdempotencyRecord(IdempotencyClaim.InFlight, null, null);
    }

    public async Task CompleteAsync(string scope, string key, string? response, string? error, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);

        // Guarded on in_progress so a late writer cannot overwrite a settled answer. In practice only
        // the claim holder gets here, which is exactly why no second writer is expected.
        await db.ExecuteAsync(new CommandDefinition(
            @"update idempotency_keys
              set status = @Completed, response_body = @Response, response_error = @Error, completed_at = @Now
              where scope = @Scope and key = @Key and status = @InProgress",
            new { Scope = scope, Key = key, Response = response, Error = error, Now = now, Completed, InProgress },
            cancellationToken: ct));
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.ExecuteAsync(new CommandDefinition(
            "delete from idempotency_keys where created_at < @Cutoff",
            new { Cutoff = cutoff },
            cancellationToken: ct));
    }

    private sealed record ClaimRow(string RequestHash, string Status, string? ResponseBody, string? ResponseError);
}
