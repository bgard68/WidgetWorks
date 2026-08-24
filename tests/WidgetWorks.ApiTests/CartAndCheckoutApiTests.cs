using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WidgetWorks.ApiTests;

[Collection(ApiCollection.Name)]
public class CartAndCheckoutApiTests(ApiFixture api)
{
    private static async Task<Guid> AnyWidgetIdAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/catalog/widgets?pageSize=1");
        return page.GetProperty("items")[0].GetProperty("id").GetGuid();
    }

    private static async Task<Guid> NewCartAsync(HttpClient client, Guid widgetId, int quantity = 2)
    {
        var response = await client.PostAsJsonAsync("/cart/items", new { cartId = (Guid?)null, widgetId, quantity });
        response.EnsureSuccessStatusCode();
        var cart = await response.Content.ReadFromJsonAsync<JsonElement>();
        return cart.GetProperty("id").GetGuid();
    }

    private static object CheckoutBody(Guid cartId, string? paymentToken = "tok_ok", string email = "guest@widgetworks.test") => new
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
        paymentToken,
    };

    [Fact]
    public async Task A_guest_builds_a_cart_updates_it_and_reads_it_back()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);

        var update = await client.PutAsJsonAsync($"/cart/{cartId}/items/{widgetId}", new { quantity = 3 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var cart = await client.GetFromJsonAsync<JsonElement>($"/cart/{cartId}");
        var line = cart.GetProperty("items")[0];
        Assert.Equal(3, line.GetProperty("quantity").GetInt32());
        Assert.True(cart.GetProperty("subtotal").GetDecimal() > 0);

        var remove = await client.DeleteAsync($"/cart/{cartId}/items/{widgetId}");
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        var emptied = await remove.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, emptied.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_cart_is_a_404_and_a_bad_add_is_a_400()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/cart/{Guid.NewGuid()}")).StatusCode);

        var badAdd = await client.PostAsJsonAsync("/cart/items", new { cartId = (Guid?)null, widgetId, quantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badAdd.StatusCode);
    }

    [Fact]
    public async Task Merging_requires_sign_in_and_folds_the_guest_cart_into_the_users()
    {
        using var guest = api.Client();
        var widgetId = await AnyWidgetIdAsync(guest);
        var guestCartId = await NewCartAsync(guest, widgetId, quantity: 2);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync("/cart/merge", new { guestCartId })).StatusCode);

        var (customer, _) = await api.FreshCustomerAsync();
        using var _customer = customer;
        var merged = await customer.PostAsJsonAsync("/cart/merge", new { guestCartId });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var cart = await merged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, cart.GetProperty("items")[0].GetProperty("quantity").GetInt32());

        // The guest cart is gone once absorbed.
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/cart/{guestCartId}")).StatusCode);
    }

    [Fact]
    public async Task Shipping_methods_tax_info_and_quotes_feed_the_checkout_page()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);

        var methods = await client.GetFromJsonAsync<string[]>("/checkout/shipping-methods");
        Assert.NotNull(methods);
        Assert.Contains("Standard", methods);
        Assert.Contains("Express", methods);

        var taxInfo = await client.GetFromJsonAsync<JsonElement>("/checkout/tax-info");
        Assert.True(taxInfo.GetProperty("stateCount").GetInt32() >= 51);

        var quote = await client.PostAsJsonAsync("/checkout/quote", new { cartId, stateCode = "CA", shippingMethod = "Standard" });
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        var body = await quote.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0.0725m, body.GetProperty("taxRate").GetDecimal());
        Assert.True(body.GetProperty("total").GetDecimal() > body.GetProperty("subtotal").GetDecimal());

        var badQuote = await client.PostAsJsonAsync("/checkout/quote", new { cartId = Guid.NewGuid(), stateCode = "CA", shippingMethod = "Standard" });
        Assert.Equal(HttpStatusCode.BadRequest, badQuote.StatusCode);
    }

    [Fact]
    public async Task A_guest_checkout_pays_and_returns_the_receipt()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);

        var response = await client.PostAsJsonAsync("/checkout", CheckoutBody(cartId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paid", receipt.GetProperty("status").GetString());
        Assert.StartsWith("WW-", receipt.GetProperty("orderNumber").GetString());

        // The cart was consumed by the order.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/cart/{cartId}")).StatusCode);
    }

    [Fact]
    public async Task A_declined_card_is_a_400_and_keeps_the_cart()
    {
        using var client = api.Client();
        var widgetId = await AnyWidgetIdAsync(client);
        var cartId = await NewCartAsync(client, widgetId);

        var response = await client.PostAsJsonAsync("/checkout", CheckoutBody(cartId, paymentToken: "tok_decline"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/cart/{cartId}")).StatusCode);
    }
}
