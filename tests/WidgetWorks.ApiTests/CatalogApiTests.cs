using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WidgetWorks.ApiTests;

[Collection(ApiCollection.Name)]
public class CatalogApiTests(ApiFixture api)
{
    private static async Task<Guid> CreateWidgetAsync(HttpClient asManager, decimal price = 5m, int onHand = 10)
    {
        var response = await asManager.PostAsJsonAsync("/admin/catalog/widgets", new
        {
            sku = "API-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            name = "Api Widget " + Guid.NewGuid().ToString("N")[..6],
            description = "Made by the API suite.",
            imageUrl = (string?)null,
            price,
            quantityOnHand = onHand,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_storefront_lists_seeded_widgets_with_paging_metadata()
    {
        using var client = api.Client();

        var page = await client.GetFromJsonAsync<JsonElement>("/catalog/widgets?page=1&pageSize=3");

        Assert.True(page.GetProperty("totalCount").GetInt32() >= 5);   // the seeded demo catalog
        Assert.Equal(3, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_widget_detail_is_public_and_an_unknown_id_is_a_404()
    {
        using var client = api.Client();
        var page = await client.GetFromJsonAsync<JsonElement>("/catalog/widgets?pageSize=1");
        var id = page.GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/catalog/widgets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/catalog/widgets/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Admin_catalog_requires_a_manager_or_administrator()
    {
        using var anonymous = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/catalog/widgets")).StatusCode);

        using var customer = await api.SignedInAsync(ApiFixture.CustomerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/admin/catalog/widgets")).StatusCode);

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/admin/catalog/widgets")).StatusCode);
    }

    [Fact]
    public async Task A_manager_can_create_edit_hide_and_restock_a_widget()
    {
        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var id = await CreateWidgetAsync(manager);

        var update = await manager.PutAsJsonAsync($"/admin/catalog/widgets/{id}", new
        {
            name = "Renamed",
            description = "Edited.",
            imageUrl = (string?)null,
            price = 6.50m,
            isActive = false,
        });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        // Hidden from the storefront, still visible to staff.
        using var shopper = api.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.GetAsync($"/catalog/widgets/{id}")).StatusCode);
        var staffView = await manager.GetFromJsonAsync<JsonElement>($"/admin/catalog/widgets/{id}");
        Assert.Equal("Renamed", staffView.GetProperty("name").GetString());

        var restock = await manager.PostAsJsonAsync($"/admin/catalog/widgets/{id}/inventory", new { quantityOnHandDelta = 5 });
        Assert.Equal(HttpStatusCode.OK, restock.StatusCode);
        var body = await restock.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(15, body.GetProperty("quantityAvailable").GetInt32());
    }

    [Fact]
    public async Task Invalid_catalog_writes_are_400s_with_reasons()
    {
        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);

        var negative = await manager.PostAsJsonAsync("/admin/catalog/widgets", new
        {
            sku = "API-BAD",
            name = "Bad",
            description = "",
            imageUrl = (string?)null,
            price = -1m,
            quantityOnHand = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        var unknownUpdate = await manager.PutAsJsonAsync($"/admin/catalog/widgets/{Guid.NewGuid()}", new
        {
            name = "X",
            description = "",
            imageUrl = (string?)null,
            price = 1m,
            isActive = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownUpdate.StatusCode);

        var badRestock = await manager.PostAsJsonAsync($"/admin/catalog/widgets/{Guid.NewGuid()}/inventory", new { quantityOnHandDelta = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, badRestock.StatusCode);
    }

    [Fact]
    public async Task Only_an_administrator_may_delete_a_widget()
    {
        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        var id = await CreateWidgetAsync(manager);

        // The whole point of the DeleteCatalog policy: a manager can hide but not retire.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync($"/admin/catalog/widgets/{id}")).StatusCode);

        using var admin = await api.SignedInAsync(ApiFixture.AdminEmail);
        var deleted = await admin.DeleteAsync($"/admin/catalog/widgets/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var body = await deleted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Deleted", body.GetProperty("outcome").GetString());   // never ordered -> hard delete
        Assert.Equal(0, body.GetProperty("orderLineCount").GetInt32());
    }
}
