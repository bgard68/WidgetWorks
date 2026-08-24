using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WidgetWorks.ApiTests;

[Collection(ApiCollection.Name)]
public class WebhookAndHealthApiTests(ApiFixture api)
{
    [Fact]
    public async Task Health_reports_ok_once_migrations_have_run()
    {
        using var client = api.Client();

        var body = await client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_webhook_for_an_unknown_provider_is_a_404()
    {
        using var client = api.Client();

        var response = await client.PostAsync("/webhooks/payments/braintree",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"outcome\":\"succeeded\"}")]   // missing reference
    public async Task An_unparseable_webhook_is_a_400(string payload)
    {
        using var client = api.Client();

        var response = await client.PostAsync("/webhooks/payments/mock",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_async_payment_settles_through_the_webhook()
    {
        using var client = api.Client();
        var page = await client.GetFromJsonAsync<JsonElement>("/catalog/widgets?pageSize=1");
        var widgetId = page.GetProperty("items")[0].GetProperty("id").GetGuid();
        var add = await client.PostAsJsonAsync("/cart/items", new { cartId = (Guid?)null, widgetId, quantity = 1 });
        var cartId = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A BNPL-style token parks the order awaiting the provider's webhook.
        var checkout = await client.PostAsJsonAsync("/checkout", new
        {
            cartId,
            email = "bnpl@widgetworks.test",
            name = "Jane Doe",
            line1 = "1 Main St",
            line2 = (string?)null,
            city = "Springfield",
            state = "CA",
            postalCode = "90001",
            country = "US",
            shippingMethod = "Standard",
            paymentToken = "klarna_demo",
        });
        checkout.EnsureSuccessStatusCode();
        var parked = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingPayment", parked.GetProperty("status").GetString());
        var reference = parked.GetProperty("paymentReference").GetString();

        // The provider calls back; the order settles.
        var webhook = await client.PostAsync("/webhooks/payments/mock",
            new StringContent($"{{\"reference\":\"{reference}\",\"outcome\":\"succeeded\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.Equal("Paid", (await webhook.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // A replay of the same webhook is acknowledged without changing anything.
        var replay = await client.PostAsync("/webhooks/payments/mock",
            new StringContent($"{{\"reference\":\"{reference}\",\"outcome\":\"succeeded\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // An event for a reference no order carries is acknowledged as ignored.
        var stray = await client.PostAsync("/webhooks/payments/mock",
            new StringContent("{\"reference\":\"mock_pi_unknown\",\"outcome\":\"succeeded\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, stray.StatusCode);
        Assert.Equal("ignored", (await stray.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
}
