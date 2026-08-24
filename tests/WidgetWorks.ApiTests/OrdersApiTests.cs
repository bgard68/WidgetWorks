using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WidgetWorks.ApiTests;

[Collection(ApiCollection.Name)]
public class OrdersApiTests(ApiFixture api)
{
    /// <summary>Places a paid order through the API as the given client and returns the receipt.</summary>
    private static async Task<JsonElement> PlaceOrderAsync(HttpClient client, string email)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/catalog/widgets?pageSize=1");
        var widgetId = page.GetProperty("items")[0].GetProperty("id").GetGuid();

        var add = await client.PostAsJsonAsync("/cart/items", new { cartId = (Guid?)null, widgetId, quantity = 1 });
        add.EnsureSuccessStatusCode();
        var cartId = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

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
    public async Task A_customer_sees_their_own_orders_and_nobody_elses()
    {
        var (customer, email) = await api.FreshCustomerAsync();
        using var _customer = customer;
        var receipt = await PlaceOrderAsync(customer, email);
        var orderId = receipt.GetProperty("orderId").GetGuid();

        var list = await customer.GetFromJsonAsync<JsonElement>("/orders");
        Assert.Contains(list.EnumerateArray(), o => o.GetProperty("id").GetGuid() == orderId);

        var detail = await customer.GetFromJsonAsync<JsonElement>($"/orders/{orderId}");
        Assert.Equal("Paid", detail.GetProperty("status").GetString());

        // A different customer cannot open it.
        var (other, _) = await api.FreshCustomerAsync();
        using var _other = other;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/orders/{orderId}")).StatusCode);
    }

    [Fact]
    public async Task A_guest_tracks_an_order_by_number_and_email_only()
    {
        using var guest = api.Client();
        var receipt = await PlaceOrderAsync(guest, "tracked-guest@widgetworks.test");
        var number = receipt.GetProperty("orderNumber").GetString();

        var found = await guest.GetAsync($"/orders/lookup?number={number}&email=tracked-guest@widgetworks.test");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        var wrongEmail = await guest.GetAsync($"/orders/lookup?number={number}&email=wrong@widgetworks.test");
        Assert.Equal(HttpStatusCode.NotFound, wrongEmail.StatusCode);
    }

    [Fact]
    public async Task Staff_list_open_and_progress_orders_customers_cannot()
    {
        using var guest = api.Client();
        var receipt = await PlaceOrderAsync(guest, "fulfilment@widgetworks.test");
        var orderId = receipt.GetProperty("orderId").GetGuid();

        using var customer = await api.SignedInAsync(ApiFixture.CustomerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/admin/orders/")).StatusCode);

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var list = await manager.GetFromJsonAsync<JsonElement>("/admin/orders/?limit=100");
        Assert.Contains(list.EnumerateArray(), o => o.GetProperty("id").GetGuid() == orderId);

        var detail = await manager.GetFromJsonAsync<JsonElement>($"/admin/orders/{orderId}");
        Assert.Equal("fulfilment@widgetworks.test", detail.GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/admin/orders/{Guid.NewGuid()}")).StatusCode);

        var shipped = await manager.PostAsJsonAsync($"/admin/orders/{orderId}/status", new { status = "Shipped", trackingNumber = "1Z999" });
        Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
        var view = await shipped.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Shipped", view.GetProperty("status").GetString());
        Assert.Equal("1Z999", view.GetProperty("trackingNumber").GetString());

        // The order state machine still rules: Shipped cannot jump back to Pending.
        var illegal = await manager.PostAsJsonAsync($"/admin/orders/{orderId}/status", new { status = "Pending", trackingNumber = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, illegal.StatusCode);
    }
}
