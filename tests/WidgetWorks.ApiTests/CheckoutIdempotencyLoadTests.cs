using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace WidgetWorks.ApiTests;

/// <summary>
/// POST /checkout under duplicate and concurrent load, over HTTP, against a real database.
///
/// The unit suite proves the handler honours the ledger and the integration suite proves the ledger
/// is atomic. Neither can answer the question an operator actually has: when fifty copies of one
/// request arrive at once through the real pipeline — routing, model binding, throttling, connection
/// pool and all — how many orders exist afterwards? That number is read back from the database here,
/// not from the API, because an API agreeing with itself proves nothing.
///
/// Load is kept to what an in-process test server can run honestly. These are correctness tests with
/// contention, not a benchmark: the elapsed times are reported for context and never asserted on,
/// because a timing threshold on shared CI hardware is a test that fails for reasons unrelated to
/// the code.
/// </summary>
[Collection(ApiCollection.Name)]
public class CheckoutIdempotencyLoadTests(ApiFixture api, ITestOutputHelper output)
{
    /// <summary>
    /// In-flight requests allowed at once. Above the pooled connection count a checkout would start
    /// queueing for a connection rather than for the thing under test, and the suite would be
    /// measuring Npgsql instead of the ledger.
    /// </summary>
    private const int MaxInFlight = 24;

    private static async Task<Guid> AnyWidgetIdAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/catalog/widgets?pageSize=1");
        return page.GetProperty("items")[0].GetProperty("id").GetGuid();
    }

    private static async Task<Guid> NewCartAsync(HttpClient client, Guid widgetId, int quantity = 1)
    {
        var response = await client.PostAsJsonAsync("/cart/items", new { cartId = (Guid?)null, widgetId, quantity });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static object Body(Guid cartId, string email) => new
    {
        cartId,
        email,
        name = "Jane Doe",
        line1 = "1 Main St",
        line2 = (string?)null,
        city = "Springfield",
        state = "CA",
        postalCode = "90001",
        country = "US",
        shippingMethod = "Standard",
        paymentToken = "tok_ok",
    };

    /// <summary>One keyed checkout, returning everything the assertions need about the answer.</summary>
    private static async Task<Attempt> CheckoutAsync(HttpClient client, Guid cartId, string email, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/checkout")
        {
            Content = JsonContent.Create(Body(cartId, email)),
        };
        request.Headers.Add("Idempotency-Key", key);

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        string? orderNumber = null;
        if (response.IsSuccessStatusCode)
        {
            orderNumber = JsonDocument.Parse(payload).RootElement.GetProperty("orderNumber").GetString();
        }

        string? code = null;
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            code = JsonDocument.Parse(payload).RootElement.GetProperty("code").GetString();
        }

        return new Attempt(
            response.StatusCode,
            orderNumber,
            response.Headers.TryGetValues("Idempotent-Replay", out var replay) && replay.Contains("true"),
            code);
    }

    private sealed record Attempt(HttpStatusCode Status, string? OrderNumber, bool Replayed, string? Code);

    /// <summary>
    /// One checkout, retried through a transient conflict the way the SPA does: a 409 carrying
    /// <c>checkout_in_flight</c> means the order is being placed by an earlier copy, so wait and ask
    /// again under the same key. Bounded, and never retried for a conflict that waiting cannot fix.
    /// </summary>
    private static async Task<Attempt> CheckoutWithRetryAsync(HttpClient client, Guid cartId, string email, string key)
    {
        int[] delays = [50, 150, 400, 1000];

        for (var attempt = 0; ; attempt++)
        {
            var result = await CheckoutAsync(client, cartId, email, key);

            if (result.Status != HttpStatusCode.Conflict ||
                result.Code != "checkout_in_flight" ||
                attempt >= delays.Length)
            {
                return result;
            }

            await Task.Delay(delays[attempt]);
        }
    }

    /// <summary>Orders recorded for an email, straight from the table.</summary>
    private async Task<int> OrderCountAsync(string email)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from orders where email = @Email", new { Email = email });
    }

    /// <summary>Runs <paramref name="work"/> for each item, capped at <see cref="MaxInFlight"/>.</summary>
    private static async Task<T[]> StormAsync<T>(int count, Func<int, Task<T>> work)
    {
        using var inFlight = new SemaphoreSlim(MaxInFlight);

        return await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            await inFlight.WaitAsync();
            try
            {
                return await work(i);
            }
            finally
            {
                inFlight.Release();
            }
        }));
    }

    [Fact]
    public async Task Fifty_simultaneous_copies_of_one_checkout_place_exactly_one_order()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);
        var email = $"storm-{Guid.NewGuid():N}@widgetworks.test";
        var key = Guid.NewGuid().ToString();

        const int Copies = 50;
        var clock = Stopwatch.StartNew();
        var attempts = await StormAsync(Copies, _ => CheckoutAsync(client, cartId, email, key));
        clock.Stop();

        var accepted = attempts.Where(a => a.Status == HttpStatusCode.OK).ToList();
        var conflicted = attempts.Where(a => a.Status == HttpStatusCode.Conflict).ToList();

        output.WriteLine(
            $"{Copies} copies in {clock.ElapsedMilliseconds} ms: {accepted.Count} OK " +
            $"({accepted.Count(a => a.Replayed)} replayed), {conflicted.Count} conflict.");

        // The whole point, measured where it counts rather than inferred from the responses.
        Assert.Equal(1, await OrderCountAsync(email));

        // Every answer was accounted for: nothing 500ed, nothing 400ed.
        Assert.Equal(Copies, accepted.Count + conflicted.Count);

        // Exactly one caller did the work; anyone else who got a 200 got it by replay.
        Assert.Equal(1, accepted.Count(a => !a.Replayed));

        // And nobody was handed a different order number.
        Assert.Single(accepted.Select(a => a.OrderNumber).Distinct());
    }

    [Fact]
    public async Task Every_one_of_fifty_callers_ends_up_holding_the_same_order_number()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);
        var email = $"recover-{Guid.NewGuid():N}@widgetworks.test";
        var key = Guid.NewGuid().ToString();

        // The same burst as above, but each caller behaves like a client that honours the conflict
        // code instead of giving up on it. One order is the floor; this is the ceiling — nobody is
        // left not knowing what happened to their money.
        const int Copies = 50;
        var clock = Stopwatch.StartNew();
        var attempts = await StormAsync(Copies, _ => CheckoutWithRetryAsync(client, cartId, email, key));
        clock.Stop();

        output.WriteLine(
            $"{Copies} copies with retry in {clock.ElapsedMilliseconds} ms: " +
            $"{attempts.Count(a => a.Status == HttpStatusCode.OK)} OK " +
            $"({attempts.Count(a => a.Replayed)} replayed), " +
            $"{attempts.Count(a => a.Status == HttpStatusCode.Conflict)} still conflicting.");

        Assert.Equal(1, await OrderCountAsync(email));
        Assert.All(attempts, a => Assert.Equal(HttpStatusCode.OK, a.Status));
        Assert.Single(attempts.Select(a => a.OrderNumber).Distinct());
        Assert.Equal(1, attempts.Count(a => !a.Replayed));
    }

    [Fact]
    public async Task A_client_that_lost_the_response_recovers_its_order_by_retrying_the_same_key()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);
        var email = $"retry-{Guid.NewGuid():N}@widgetworks.test";
        var key = Guid.NewGuid().ToString();

        var first = await CheckoutAsync(client, cartId, email, key);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.False(first.Replayed);

        // The retry a dropped connection produces. Note what it is recovering from: checkout deleted
        // the cart on success, so without the ledger this same request could only ever answer
        // "Cart not found" — the client would be left unable to learn its own order number.
        var retry = await CheckoutAsync(client, cartId, email, key);

        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.True(retry.Replayed);
        Assert.Equal(first.OrderNumber, retry.OrderNumber);
        Assert.Equal(1, await OrderCountAsync(email));
    }

    [Fact]
    public async Task A_key_reused_for_a_different_basket_is_refused()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);
        var email = $"reuse-{Guid.NewGuid():N}@widgetworks.test";
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.OK, (await CheckoutAsync(client, cartId, email, key)).Status);

        // Same key, same cart, different customer. Replaying here would disclose one shopper's order
        // to another; 409 is the only safe answer.
        var reused = await CheckoutAsync(client, cartId, "someone-else@widgetworks.test", key);
        Assert.Equal(HttpStatusCode.Conflict, reused.Status);

        // Flagged as permanent, so a retrying client knows not to wait on it.
        Assert.Equal("idempotency_key_reused", reused.Code);
        Assert.Equal(0, await OrderCountAsync("someone-else@widgetworks.test"));
    }

    [Fact]
    public async Task Heavy_mixed_traffic_settles_at_one_order_per_shopper()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);

        // Forty shoppers, each of whom submits three times — the realistic shape of this load, where
        // duplicates are spread across unrelated baskets rather than aimed at one. It also catches
        // the opposite failure to a missing ledger: a scope so coarse that one shopper's key
        // suppresses another's perfectly legitimate order.
        const int Shoppers = 40;
        const int CopiesEach = 3;

        var shoppers = new List<(Guid CartId, string Email, string Key)>();
        for (var i = 0; i < Shoppers; i++)
        {
            shoppers.Add((
                await NewCartAsync(client, widgetId),
                $"mixed-{Guid.NewGuid():N}@widgetworks.test",
                Guid.NewGuid().ToString()));
        }

        var clock = Stopwatch.StartNew();
        var attempts = await StormAsync(Shoppers * CopiesEach, i =>
        {
            var shopper = shoppers[i % Shoppers];
            return CheckoutAsync(client, shopper.CartId, shopper.Email, shopper.Key);
        });
        clock.Stop();

        var accepted = attempts.Where(a => a.Status == HttpStatusCode.OK).ToList();
        output.WriteLine(
            $"{attempts.Length} requests across {Shoppers} shoppers in {clock.ElapsedMilliseconds} ms: " +
            $"{accepted.Count} OK ({accepted.Count(a => a.Replayed)} replayed), " +
            $"{attempts.Count(a => a.Status == HttpStatusCode.Conflict)} conflict.");

        // No request was lost or errored, and every shopper got exactly one order — not nought,
        // which would mean cross-talk between keys, and not two, which would mean no protection.
        Assert.All(attempts, a => Assert.Contains(a.Status, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(Shoppers, accepted.Count(a => !a.Replayed));
        Assert.Equal(Shoppers, accepted.Select(a => a.OrderNumber).Distinct().Count());

        var counts = await Task.WhenAll(shoppers.Select(s => OrderCountAsync(s.Email)));
        Assert.All(counts, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task An_unkeyed_checkout_still_works_so_existing_clients_are_unaffected()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);
        var email = $"unkeyed-{Guid.NewGuid():N}@widgetworks.test";

        // No Idempotency-Key header at all: the old contract, unchanged — and unprotected. The
        // duplicate hole that leaves open is pinned down deterministically in the unit suite rather
        // than raced for here.
        var response = await client.PostAsJsonAsync("/checkout", Body(cartId, email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replay"));
        Assert.Equal(1, await OrderCountAsync(email));
    }
}
