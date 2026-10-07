using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The staff side of the payment path over HTTP: the review list, and refunds.
///
/// These are the routes that let a person act on what the background machinery cannot decide. Two
/// things matter more than the happy path — that they are behind staff authorization, since one of
/// them moves money and the other exposes customer emails; and that a refund is reachable only through
/// its own route, never by posting a status.
/// </summary>
[Collection(ApiCollection.Name)]
public class PaymentOperationsApiTests(ApiFixture api)
{
    private static async Task<Guid> AnyWidgetIdAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/catalog/widgets?pageSize=1");
        return page.GetProperty("items")[0].GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> PlaceOrderAsync(HttpClient client, string email)
    {
        var widgetId = await AnyWidgetIdAsync(client);
        var cart = await client.PostAsJsonAsync("/cart/items", new { cartId = (Guid?)null, widgetId, quantity = 1 });
        cart.EnsureSuccessStatusCode();
        var cartId = (await cart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var checkout = await client.PostAsJsonAsync("/checkout", new
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
        });
        checkout.EnsureSuccessStatusCode();
        return await checkout.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_review_list_needs_staff_rights_because_it_exposes_customer_emails()
    {
        using var guest = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/admin/orders/payment-exceptions")).StatusCode);

        var (customer, _) = await api.FreshCustomerAsync();
        using (customer)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/admin/orders/payment-exceptions")).StatusCode);
        }

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var allowed = await manager.GetAsync("/admin/orders/payment-exceptions");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // Both halves are always present, so a client never has to guess whether an absent key means
        // "none" or "not supported".
        var body = await allowed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("unconfirmed").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("possibleDuplicates").ValueKind);
    }

    [Fact]
    public async Task Two_identical_orders_in_quick_succession_show_up_for_review()
    {
        var email = $"dup-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();

        // Two separate carts, two separate keys — genuinely two orders, which is exactly what staff
        // need to see: idempotency cannot catch this, because nothing says they are the same request.
        var first = await PlaceOrderAsync(client, email);
        var second = await PlaceOrderAsync(client, email);

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var body = await manager.GetFromJsonAsync<JsonElement>("/admin/orders/payment-exceptions");

        var flagged = body.GetProperty("possibleDuplicates").EnumerateArray()
            .Select(o => o.GetProperty("orderNumber").GetString())
            .ToList();

        Assert.Contains(first.GetProperty("orderNumber").GetString(), flagged);
        Assert.Contains(second.GetProperty("orderNumber").GetString(), flagged);
    }

    [Fact]
    public async Task A_refund_gives_the_money_back_and_is_idempotent()
    {
        var email = $"refund-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        var order = await PlaceOrderAsync(client, email);
        var orderId = order.GetProperty("orderId").GetGuid();

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);

        var refunded = await manager.PostAsync($"/admin/orders/{orderId}/refund", null);
        Assert.Equal(HttpStatusCode.OK, refunded.StatusCode);
        Assert.Equal("Refunded", (await refunded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // Clicking again is a no-op, not a second payout.
        var again = await manager.PostAsync($"/admin/orders/{orderId}/refund", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Refunded", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Refunding_needs_staff_rights_and_a_real_order()
    {
        using var guest = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsync($"/admin/orders/{Guid.NewGuid()}/refund", null)).StatusCode);

        var (customer, _) = await api.FreshCustomerAsync();
        using (customer)
        {
            // A customer refunding their own order at will is the obvious abuse; the policy is what
            // stops it.
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/admin/orders/{Guid.NewGuid()}/refund", null)).StatusCode);
        }

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var missing = await manager.PostAsync($"/admin/orders/{Guid.NewGuid()}/refund", null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("not found", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_refund_cannot_be_faked_through_the_status_endpoint()
    {
        var email = $"status-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        var order = await PlaceOrderAsync(client, email);
        var orderId = order.GetProperty("orderId").GetGuid();

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var posted = await manager.PostAsJsonAsync($"/admin/orders/{orderId}/status", new { status = "Refunded", trackingNumber = (string?)null });

        // Refused, because that endpoint cannot move money and so must not be able to claim it did.
        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);

        var unchanged = await manager.GetFromJsonAsync<JsonElement>($"/admin/orders/{orderId}");
        Assert.Equal("Paid", unchanged.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_shipped_order_is_refused_rather_than_half_refunded()
    {
        var email = $"shipped-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        var order = await PlaceOrderAsync(client, email);
        var orderId = order.GetProperty("orderId").GetGuid();

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var shipped = await manager.PostAsJsonAsync($"/admin/orders/{orderId}/status", new { status = "Shipped", trackingNumber = "TRK-1" });
        shipped.EnsureSuccessStatusCode();

        var refund = await manager.PostAsync($"/admin/orders/{orderId}/refund", null);

        // The goods are in transit, so money alone is only half the question — that is a returns
        // workflow, and this route says so instead of pretending.
        Assert.Equal(HttpStatusCode.BadRequest, refund.StatusCode);
        Assert.Contains("Only a paid order", (await refund.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_partial_refund_leaves_the_order_paid_with_the_amount_recorded()
    {
        var email = $"partial-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        var order = await PlaceOrderAsync(client, email);
        var orderId = order.GetProperty("orderId").GetGuid();
        var total = order.GetProperty("total").GetDecimal();

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);

        var partial = await manager.PostAsJsonAsync($"/admin/orders/{orderId}/refund", new { amount = 5m });
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);

        var body = await partial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paid", body.GetProperty("status").GetString());
        Assert.Equal(5m, body.GetProperty("refundedTotal").GetDecimal());

        // The balance settles it.
        var rest = await manager.PostAsJsonAsync($"/admin/orders/{orderId}/refund", new { amount = total - 5m });
        Assert.Equal("Refunded", (await rest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Refunding_more_than_the_order_owes_is_refused()
    {
        var email = $"over-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        var order = await PlaceOrderAsync(client, email);
        var orderId = order.GetProperty("orderId").GetGuid();

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var tooMuch = await manager.PostAsJsonAsync($"/admin/orders/{orderId}/refund", new { amount = 10_000m });

        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Contains("remains refundable", (await tooMuch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Refunds_and_status_changes_land_in_the_audit_trail_with_the_staff_member_who_made_them()
    {
        var email = $"audit-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        var order = await PlaceOrderAsync(client, email);
        var orderId = order.GetProperty("orderId").GetGuid();
        var orderNumber = order.GetProperty("orderNumber").GetString();

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        (await manager.PostAsync($"/admin/orders/{orderId}/refund", null)).EnsureSuccessStatusCode();

        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();

        // Read back from audit_events, because a log line is not a trail: nobody queries stdout a month
        // later when asked who refunded an order.
        var entries = (await connection.QueryAsync<(string Action, string? Detail, Guid? UserId)>(
            @"select a.action, a.detail, a.user_id
              from audit_events a
              where a.action like 'order.%' and a.detail like @Pattern",
            new { Pattern = "%" + orderNumber + "%" })).ToList();

        var refund = Assert.Single(entries, e => e.Action == "order.refunded");
        Assert.Contains(orderNumber!, refund.Detail);

        // Attributed to a real user, not to nobody.
        Assert.NotNull(refund.UserId);

        var staffId = await connection.ExecuteScalarAsync<Guid>(
            "select id from users where normalized_email = @Email",
            new { Email = ApiFixture.ManagerEmail.ToUpperInvariant() });
        Assert.Equal(staffId, refund.UserId);
    }

    [Fact]
    public async Task A_status_change_is_attributed_to_whoever_made_it()
    {
        var email = $"audit-status-{Guid.NewGuid():N}@widgetworks.test";
        using var client = api.Client();
        var order = await PlaceOrderAsync(client, email);
        var orderId = order.GetProperty("orderId").GetGuid();
        var orderNumber = order.GetProperty("orderNumber").GetString();

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        (await manager.PostAsJsonAsync($"/admin/orders/{orderId}/status", new { status = "Shipped", trackingNumber = "TRK-9" }))
            .EnsureSuccessStatusCode();

        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();

        var detail = await connection.ExecuteScalarAsync<string>(
            @"select detail from audit_events
              where action = 'order.status_changed' and detail like @Pattern",
            new { Pattern = "%" + orderNumber + "%" });

        // Records what it moved from as well as to — "who changed this" is useless without "from what".
        Assert.Contains("Paid -> Shipped", detail!);
    }
}
