using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The account area over HTTP, plus the two staff routes that exist because of it.
///
/// What this suite is really for is the boundaries: who may call what. A profile is only ever your
/// own, a password change needs the old one, resetting someone else's second factor is an
/// Administrator-only act, and order search must not become a way to browse the customer list.
/// </summary>
[Collection(ApiCollection.Name)]
public class AccountApiTests(ApiFixture api)
{
    [Fact]
    public async Task A_profile_is_yours_and_nobody_elses()
    {
        using var guest = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/account/profile")).StatusCode);

        var (customer, email) = await api.FreshCustomerAsync();
        using (customer)
        {
            var profile = await customer.GetFromJsonAsync<JsonElement>("/account/profile");

            // There is no id in the route — the profile is read from the token, so one customer
            // cannot ask for another's by changing a number in a URL.
            Assert.Equal(email, profile.GetProperty("email").GetString());
            Assert.Equal("Customer", profile.GetProperty("role").GetString());
            Assert.True(profile.GetProperty("hasPassword").GetBoolean());
            Assert.Equal(JsonValueKind.Null, profile.GetProperty("displayName").ValueKind);
        }
    }

    [Fact]
    public async Task A_name_can_be_set_cleared_and_is_reflected_back()
    {
        var (customer, _) = await api.FreshCustomerAsync();
        using (customer)
        {
            var set = await customer.PutAsJsonAsync("/account/profile", new { displayName = "  Jane Doe  " });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal("Jane Doe", (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayName").GetString());

            // Blank clears it rather than storing an empty string, so the greeting falls back to a
            // neutral one instead of rendering "Hello, " with nothing after it.
            var cleared = await customer.PutAsJsonAsync("/account/profile", new { displayName = "   " });
            Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayName").ValueKind);
        }
    }

    [Fact]
    public async Task Changing_a_password_returns_a_working_session_and_kills_the_old_one()
    {
        var (customer, email) = await api.FreshCustomerAsync();
        using (customer)
        {
            const string next = "An0ther!Passw0rd";

            var changed = await customer.PostAsJsonAsync("/account/password",
                new { currentPassword = ApiFixture.Password, newPassword = next });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

            var tokens = await changed.Content.ReadFromJsonAsync<JsonElement>();
            var fresh = tokens.GetProperty("accessToken").GetString();

            // The old access token died with the security stamp, which is the whole point — but the
            // page that just succeeded must not appear to sign you out, so a replacement comes back.
            using var withOld = api.Client();
            withOld.DefaultRequestHeaders.Authorization = customer.DefaultRequestHeaders.Authorization;
            Assert.Equal(HttpStatusCode.Unauthorized, (await withOld.GetAsync("/account/profile")).StatusCode);

            using var withNew = api.Client();
            withNew.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
            Assert.Equal(HttpStatusCode.OK, (await withNew.GetAsync("/account/profile")).StatusCode);

            // And the new password is the one that works from now on.
            using var plain = api.Client();
            var relogin = await plain.PostAsJsonAsync("/auth/login", new { email, password = next });
            Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        }
    }

    [Fact]
    public async Task A_password_change_is_refused_without_the_current_one_and_against_the_policy()
    {
        var (customer, _) = await api.FreshCustomerAsync();
        using (customer)
        {
            var wrong = await customer.PostAsJsonAsync("/account/password",
                new { currentPassword = "Wr0ng!Passw0rd", newPassword = "An0ther!Passw0rd" });
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Contains("not your current password", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

            var weak = await customer.PostAsJsonAsync("/account/password",
                new { currentPassword = ApiFixture.Password, newPassword = "short" });
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
            Assert.StartsWith("Password needs:", (await weak.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Resetting_someone_elses_two_factor_is_an_administrator_act()
    {
        var (customer, _) = await api.FreshCustomerAsync();
        using (customer)
        {
            // A customer resetting anyone's second factor — including their own by this route — is
            // the obvious abuse. The policy is what stops it.
            Assert.Equal(HttpStatusCode.Forbidden,
                (await customer.PostAsync($"/admin/users/{Guid.NewGuid()}/reset-2fa", null)).StatusCode);
        }

        // Manager is not enough either: clearing a second factor is an account-takeover primitive,
        // so it sits with user administration rather than order management.
        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.PostAsync($"/admin/users/{Guid.NewGuid()}/reset-2fa", null)).StatusCode);

        using var admin = await api.SignedInAsync(ApiFixture.AdminEmail);
        var missing = await admin.PostAsync($"/admin/users/{Guid.NewGuid()}/reset-2fa", null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Staff_can_find_an_order_by_its_number_or_the_customers_email()
    {
        var email = $"search-{Guid.NewGuid():N}@widgetworks.test";
        using var shopper = api.Client();

        var page = await shopper.GetFromJsonAsync<JsonElement>("/catalog/widgets?pageSize=1");
        var widgetId = page.GetProperty("items")[0].GetProperty("id").GetGuid();
        var cart = await shopper.PostAsJsonAsync("/cart/items", new { cartId = (Guid?)null, widgetId, quantity = 1 });
        var cartId = (await cart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var checkout = await shopper.PostAsJsonAsync("/checkout", new
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
        var orderNumber = (await checkout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderNumber").GetString()!;

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);

        // Before this existed, staff could see only the fifty most recent orders or fetch one by a
        // GUID nobody has — so a customer ringing about an older order was simply unfindable.
        var byNumber = await manager.GetFromJsonAsync<JsonElement>($"/admin/orders/search?q={orderNumber}");
        Assert.Contains(byNumber.EnumerateArray(), o => o.GetProperty("orderNumber").GetString() == orderNumber);

        // The email works too, because customers arrive with whichever they have to hand.
        var byEmail = await manager.GetFromJsonAsync<JsonElement>($"/admin/orders/search?q={Uri.EscapeDataString(email)}");
        Assert.Contains(byEmail.EnumerateArray(), o => o.GetProperty("orderNumber").GetString() == orderNumber);
    }

    [Fact]
    public async Task Order_search_is_staff_only_and_refuses_to_be_a_customer_list()
    {
        using var guest = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/admin/orders/search?q=WW-")).StatusCode);

        var (customer, _) = await api.FreshCustomerAsync();
        using (customer)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/admin/orders/search?q=WW-")).StatusCode);
        }

        using var manager = await api.SignedInAsync(ApiFixture.ManagerEmail);

        // Two characters against an email column would hand over a slice of the customer list, so a
        // term that short returns nothing rather than everything.
        var tooShort = await manager.GetFromJsonAsync<JsonElement>("/admin/orders/search?q=WW");
        Assert.Empty(tooShort.EnumerateArray());

        var empty = await manager.GetFromJsonAsync<JsonElement>("/admin/orders/search");
        Assert.Empty(empty.EnumerateArray());
    }
}
