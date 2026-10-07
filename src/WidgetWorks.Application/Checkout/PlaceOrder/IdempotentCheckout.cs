using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Checkout.PlaceOrder;

/// <summary>
/// How long a key is honoured, and how long one may be. Bound from the <c>Idempotency</c>
/// configuration section.
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>
    /// How long a key is remembered. This is the window in which a retry still gets the original
    /// order back instead of placing a second one; past it, the same key is a new request.
    ///
    /// A day is far longer than any client retry budget, which is the point — the cost of
    /// remembering too long is a few rows, and the cost of forgetting too early is a duplicate
    /// order. Keys are deleted by the sweep rather than kept forever so the table stays small
    /// enough to be uninteresting.
    /// </summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>
    /// Longest key accepted. A key is an opaque client token — a GUID is 36 characters — so this is
    /// only here to stop an unbounded header becoming an unbounded row.
    /// </summary>
    public int MaxKeyLength { get; set; } = 200;
}

/// <summary>How a checkout attempt ended, in the three shapes the HTTP layer has to tell apart.</summary>
public enum CheckoutOutcome
{
    /// <summary>An order exists. 200.</summary>
    Placed,

    /// <summary>The request was refused on its merits — empty cart, declined card. 400.</summary>
    Rejected,

    /// <summary>The key cannot be answered right now: still in flight, or reused for other content. 409.</summary>
    Conflict,
}

/// <summary>
/// Machine-readable reasons a checkout conflicted.
///
/// The prose in <c>Error</c> is for a person; a client has to decide whether trying again could ever
/// help, and it should not have to match on English to do it. Only <see cref="InFlight"/> is worth
/// retrying — the other two describe situations no amount of waiting improves.
/// </summary>
public static class CheckoutConflictCodes
{
    /// <summary>An earlier copy of this request is still running. Transient: retry the same key.</summary>
    public const string InFlight = "checkout_in_flight";

    /// <summary>The key was already used for different content. Permanent: a new key is needed.</summary>
    public const string KeyReused = "idempotency_key_reused";

    /// <summary>The stored response could not be read back. Permanent: look the order up instead.</summary>
    public const string ReplayFailed = "idempotency_replay_failed";
}

/// <summary>
/// The outcome, plus whether it came from the ledger rather than from doing the work. Replay is
/// reported so the caller can surface it on the response, which keeps a duplicate retry visible in
/// logs instead of looking like a second successful sale.
/// </summary>
public sealed record IdempotentCheckoutResult(
    CheckoutOutcome Outcome,
    CheckoutResult? Value,
    string? Error,
    bool Replayed,
    string? ConflictCode = null)
{
    public static IdempotentCheckoutResult Placed(CheckoutResult value, bool replayed = false) => new(CheckoutOutcome.Placed, value, null, replayed);

    public static IdempotentCheckoutResult Rejected(string error, bool replayed = false) => new(CheckoutOutcome.Rejected, null, error, replayed);

    public static IdempotentCheckoutResult Conflict(string error, string code) => new(CheckoutOutcome.Conflict, null, error, false, code);
}

/// <summary>
/// Makes <see cref="CheckoutHandler"/> safe to call twice.
///
/// Placing an order is the one write in this system that both charges money and consumes stock, and
/// it is reached by a POST that a browser, a proxy, an impatient shopper or a client retry policy
/// will happily send more than once. The settlement path is already duplicate-proof — it decides by
/// compare-and-set in the database — but checkout had nothing: each arrival minted a fresh order id
/// and a fresh order number, so no unique index could catch the second one.
///
/// The fix is a key the client supplies and the database arbitrates. The key is claimed before any
/// work starts, so the loser of that race never reaches the payment gateway at all, and an order
/// cannot exist without a ledger row pointing at it. The stored response is what makes this a retry
/// rather than merely a block: a caller that lost its connection mid-checkout sends the same key and
/// gets the same order number back.
///
/// Without a key the call passes straight through, unchanged. Idempotency cannot be imposed on a
/// client that has not opted in — only offered to one that has.
/// </summary>
public sealed class IdempotentCheckout(
    CheckoutHandler inner,
    IIdempotencyStore store,
    IdempotencyOptions options,
    TimeProvider clock,
    ILogger<IdempotentCheckout> logger)
{
    /// <summary>
    /// What an Idempotency-Key may be made of. Matched rather than escaped — see the note at the call
    /// site. Anchored and length-bounded so the match itself cannot be the slow part of a request.
    /// </summary>
    private static readonly Regex SafeKey = new(@"\A[A-Za-z0-9._:+-]{1,200}\z", RegexOptions.CultureInvariant);

    public async Task<IdempotentCheckoutResult> Handle(string? idempotencyKey, CheckoutCommand command, CancellationToken ct)
    {
        var key = idempotencyKey?.Trim();

        if (string.IsNullOrEmpty(key))
        {
            return Map(await inner.Handle(command, ct));
        }

        if (key.Length > options.MaxKeyLength)
        {
            return IdempotentCheckoutResult.Rejected($"Idempotency-Key must be at most {options.MaxKeyLength} characters.");
        }

        // Constrained to the characters an opaque token legitimately uses, and refused otherwise.
        //
        // This is a log-safety guard as much as a validation one. The key arrives in a request header,
        // so it is caller-controlled text, and it is written to the log below — a value carrying
        // newlines could forge whole entries and make an attacker's fiction indistinguishable from the
        // record (CWE-117). Rejecting beats escaping: nothing legitimate needs a control character in
        // an idempotency key, so the narrow set is free, and a caller is told rather than quietly having
        // its key rewritten under it.
        if (!SafeKey.IsMatch(key))
        {
            return IdempotentCheckoutResult.Rejected(
                "Idempotency-Key may contain only letters, digits, and the characters - _ . : +");
        }

        // Scoped to the cart, not globally: the key only has to be unique among attempts to buy this
        // basket, and a cart id is itself unguessable, so one shopper's key can never name another
        // shopper's order. The ledger row outlives the cart (checkout deletes it on success) because
        // nothing links the two — a retry arriving after the cart is gone still replays.
        var scope = $"checkout:{command.CartId:N}";
        var fingerprint = Fingerprint(command);

        var claim = await store.TryClaimAsync(scope, key, fingerprint, clock.GetUtcNow(), ct);

        switch (claim.Claim)
        {
            case IdempotencyClaim.Replay:
                return Replay(claim, key);

            case IdempotencyClaim.InFlight:
                // The honest answer, and the safe one. The first attempt may be at the payment
                // gateway right now; waiting on it would tie up a request thread, and proceeding
                // would be the duplicate charge this whole class exists to prevent.
                logger.LogInformation("Checkout key {Key} is already in flight; told the caller to retry.", ForLog(key));
                return IdempotentCheckoutResult.Conflict(
                    "A checkout with this Idempotency-Key is still being processed. Retry shortly.",
                    CheckoutConflictCodes.InFlight);

            case IdempotencyClaim.PayloadMismatch:
                logger.LogWarning("Checkout key {Key} was reused with a different request body.", ForLog(key));
                return IdempotentCheckoutResult.Conflict(
                    "This Idempotency-Key was already used for a different request.",
                    CheckoutConflictCodes.KeyReused);
        }

        var result = Map(await inner.Handle(command, ct));

        // Deliberately not wrapped in a try/catch that releases the claim. If the work throws we do
        // not know whether the card was charged, and the only answer that cannot double-charge is to
        // leave the key held: later retries get 409 until retention expires it. A client that needs
        // to try again can do so under a new key, having decided for itself that the first attempt
        // failed.
        await store.CompleteAsync(
            scope,
            key,
            result.Outcome == CheckoutOutcome.Placed ? JsonSerializer.Serialize(result.Value) : null,
            result.Error,
            clock.GetUtcNow(),
            ct);

        return result;
    }

    private IdempotentCheckoutResult Replay(IdempotencyRecord claim, string key)
    {
        logger.LogInformation("Replayed stored checkout response for key {Key}.", ForLog(key));

        if (claim.Response is null)
        {
            // A stored failure: the first attempt was refused, so refuse identically rather than
            // letting a retry quietly become a second attempt at paying.
            return IdempotentCheckoutResult.Rejected(claim.Error ?? "Checkout failed.", replayed: true);
        }

        var value = JsonSerializer.Deserialize<CheckoutResult>(claim.Response);
        if (value is null)
        {
            // Only reachable if the stored row was corrupted, or written by an older shape of this
            // record. Refusing is right: an order does exist, and guessing at its details would be
            // worse than telling the caller to look it up.
            logger.LogError("Stored checkout response for key {Key} could not be read back.", ForLog(key));
            return IdempotentCheckoutResult.Conflict(
                "The original response for this Idempotency-Key could not be replayed.",
                CheckoutConflictCodes.ReplayFailed);
        }

        return IdempotentCheckoutResult.Placed(value, replayed: true);
    }

    private static IdempotentCheckoutResult Map(Result<CheckoutResult> result)
        => result.IsSuccess
            ? IdempotentCheckoutResult.Placed(result.Value!)
            : IdempotentCheckoutResult.Rejected(result.Error ?? "Checkout failed.");

    /// <summary>
    /// The key as it is safe to put in a log line: line endings removed.
    ///
    /// The charset guard in <see cref="Handle"/> already refuses a key containing one, so this is belt
    /// and braces — applied at the point of output because that is where getting it wrong produces a
    /// forged record rather than a rejected request, and because the guard is a contract that a future
    /// edit could loosen while these log statements stayed put.
    /// </summary>
    private static string ForLog(string key)
        => key.Replace("\r", string.Empty, StringComparison.Ordinal)
              .Replace("\n", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Identifies the request behind a key, so reusing the key for different content is caught
    /// rather than answered with the wrong order. Hashed rather than stored: the command carries an
    /// address and an email, and the ledger has no business keeping a second copy of either.
    /// </summary>
    private static string Fingerprint(CheckoutCommand command)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command))));
}
