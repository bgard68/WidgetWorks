namespace WidgetWorks.Application.Abstractions;

/// <summary>What happened when a caller tried to claim an idempotency key.</summary>
public enum IdempotencyClaim
{
    /// <summary>First arrival. This caller owns the work and must report the outcome.</summary>
    Claimed,

    /// <summary>An earlier caller holds the key and has not finished. Nothing to replay yet.</summary>
    InFlight,

    /// <summary>The work already completed; the stored response travels back with this claim.</summary>
    Replay,

    /// <summary>The key was used before for a materially different request.</summary>
    PayloadMismatch,
}

/// <summary>
/// A claim outcome plus the stored response when there is one. <paramref name="Response"/> and
/// <paramref name="Error"/> are mutually exclusive and only populated for <see cref="IdempotencyClaim.Replay"/>.
/// </summary>
public sealed record IdempotencyRecord(IdempotencyClaim Claim, string? Response, string? Error);

/// <summary>
/// Remembers that a request was already handled, so a retry of it returns the first answer instead
/// of doing the work twice.
///
/// Claim-then-work, never work-then-record: <see cref="TryClaimAsync"/> is the atomic step, and
/// everything downstream of it is already protected by the time it runs.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Attempts to take ownership of <paramref name="key"/> within <paramref name="scope"/>. Exactly
    /// one concurrent caller can receive <see cref="IdempotencyClaim.Claimed"/>; the others are told
    /// to replay, to wait, or that they changed the payload.
    /// </summary>
    Task<IdempotencyRecord> TryClaimAsync(string scope, string key, string requestHash, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Stores the outcome against a key this caller claimed, which is what later retries replay.
    /// Failures are stored too: replaying a decline is correct, and re-running one risks a second
    /// charge for no benefit.
    /// </summary>
    Task CompleteAsync(string scope, string key, string? response, string? error, DateTimeOffset now, CancellationToken ct);

    /// <summary>Deletes keys claimed before <paramref name="cutoff"/>. Returns how many went.</summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct);
}
