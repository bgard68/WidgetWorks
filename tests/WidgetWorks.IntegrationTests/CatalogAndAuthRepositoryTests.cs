using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// Catalog, cart, and auth persistence against real PostgreSQL. These repositories lean on things
/// only a database provides — a case-folded unique index on SKU, ON CONFLICT upserts, partial
/// indexes, and cascading deletes — so a fake would prove nothing about them.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CatalogAndAuthRepositoryTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private WidgetRepository Widgets => new(db.Connections);

    private CartRepository Carts => new(db.Connections, TimeProvider.System);

    private UserRepository Users => new(db.Connections);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private async Task<Widget> GivenWidget(int onHand = 10, bool active = true, string? name = null, decimal price = 12.5m, int reserved = 0)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = Unique("SKU-").ToUpperInvariant(),
            Name = name ?? Unique("Widget "),
            Description = "Integration fixture.",
            Price = price,
            QuantityOnHand = onHand,
            QuantityReserved = reserved,
            IsActive = active,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await Widgets.AddAsync(widget, CancellationToken.None);
        return widget;
    }

    private async Task<User> GivenUser(string? role = null)
    {
        var email = Unique("it-") + "@example.com";
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            Role = role ?? UserRoles.Customer,
            SecurityStamp = Guid.NewGuid(),
            CreatedAt = Now,
        };
        await Users.AddAsync(user, CancellationToken.None);
        return user;
    }

    // ---- widgets -------------------------------------------------------------------------

    [Fact]
    public async Task A_widget_round_trips_every_column()
    {
        var widget = await GivenWidget(onHand: 7);

        var stored = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);

        Assert.Equal(widget.Sku, stored!.Sku);
        Assert.Equal(widget.Name, stored.Name);
        Assert.Equal(12.5m, stored.Price);
        Assert.Equal(7, stored.QuantityOnHand);
        Assert.Equal(0, stored.QuantityReserved);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task A_widget_can_be_found_by_its_normalized_sku()
    {
        var widget = await GivenWidget();

        Assert.NotNull(await Widgets.GetBySkuAsync(widget.Sku.ToUpperInvariant(), CancellationToken.None));
        Assert.Null(await Widgets.GetBySkuAsync("NOT-A-SKU", CancellationToken.None));
    }

    [Fact]
    public async Task Updating_a_widget_persists_the_change()
    {
        var widget = await GivenWidget();
        widget.Price = 99.99m;
        widget.QuantityOnHand = 3;
        widget.IsActive = false;

        await Widgets.UpdateAsync(widget, CancellationToken.None);

        var stored = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);
        Assert.Equal(99.99m, stored!.Price);
        Assert.Equal(3, stored.QuantityOnHand);
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task Search_matches_on_name_and_respects_the_active_filter()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await GivenWidget(name: $"Findable {token}");
        await GivenWidget(name: $"Hidden {token}", active: false);

        var all = await Widgets.SearchAsync(new WidgetQuery(token, ActiveOnly: false, 1, 50), CancellationToken.None);
        var live = await Widgets.SearchAsync(new WidgetQuery(token, ActiveOnly: true, 1, 50), CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.Single(live);
        Assert.Equal(2, await Widgets.CountAsync(new WidgetQuery(token, false, 1, 50), CancellationToken.None));
        Assert.Equal(1, await Widgets.CountAsync(new WidgetQuery(token, true, 1, 50), CancellationToken.None));
    }

    [Fact]
    public async Task Search_pages_through_results()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++)
        {
            await GivenWidget(name: $"Paged {token} {i}");
        }

        var first = await Widgets.SearchAsync(new WidgetQuery(token, true, 1, 2), CancellationToken.None);
        var second = await Widgets.SearchAsync(new WidgetQuery(token, true, 2, 2), CancellationToken.None);

        Assert.Equal(2, first.Count);
        Assert.Single(second);
        Assert.Empty(first.Select(w => w.Id).Intersect(second.Select(w => w.Id)));
    }

    [Fact]
    public async Task A_widget_that_was_never_ordered_reports_no_order_lines_and_deletes()
    {
        var widget = await GivenWidget();

        Assert.Equal(0, await Widgets.CountOrderLinesAsync(widget.Id, CancellationToken.None));

        await Widgets.DeleteAsync(widget.Id, CancellationToken.None);

        Assert.Null(await Widgets.GetByIdAsync(widget.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Two_widgets_cannot_share_a_sku_whatever_the_casing()
    {
        var widget = await GivenWidget();

        // ux_widgets_sku is on upper(sku), so the clash is caught however it is typed. This is
        // enforced by the index, not by application code, so a direct write cannot dodge it.
        var clash = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = widget.Sku.ToLowerInvariant(),
            Name = Unique("Other "),
            Description = "Should not be accepted.",
            Price = 1m,
            QuantityOnHand = 1,
            IsActive = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        await Assert.ThrowsAnyAsync<Exception>(() => Widgets.AddAsync(clash, CancellationToken.None));
    }

    [Fact]
    public async Task Widget_names_are_deliberately_not_unique()
    {
        // ix_widgets_live_name exists for ordering the live set, not to constrain it: two
        // widgets may legitimately share a display name while differing by SKU.
        var name = Unique("Shared ");
        await GivenWidget(name: name);

        var second = await GivenWidget(name: name);

        Assert.Equal(name, (await Widgets.GetByIdAsync(second.Id, CancellationToken.None))!.Name);
    }

    // ---- carts ---------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_cart_is_created_and_read_back()
    {
        var cart = await Carts.CreateAsync(null, CancellationToken.None);

        var stored = await Carts.GetAsync(cart.Id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Null(stored!.UserId);
        Assert.Empty(stored.Items);
    }

    [Fact]
    public async Task Adding_the_same_widget_twice_updates_the_line_rather_than_duplicating_it()
    {
        var widget = await GivenWidget();
        var cart = await Carts.CreateAsync(null, CancellationToken.None);

        await Carts.UpsertItemAsync(cart.Id, widget.Id, 2, Now, CancellationToken.None);
        await Carts.UpsertItemAsync(cart.Id, widget.Id, 5, Now, CancellationToken.None);

        var stored = await Carts.GetAsync(cart.Id, CancellationToken.None);
        var item = Assert.Single(stored!.Items);
        Assert.Equal(5, item.Quantity);
    }

    [Fact]
    public async Task A_users_cart_can_be_found_by_the_user()
    {
        var user = await GivenUser();
        var cart = await Carts.CreateAsync(user.Id, CancellationToken.None);

        var found = await Carts.GetByUserAsync(user.Id, CancellationToken.None);

        Assert.Equal(cart.Id, found!.Id);
        Assert.Null(await Carts.GetByUserAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Removing_an_item_and_touching_the_cart_both_persist()
    {
        var widget = await GivenWidget();
        var cart = await Carts.CreateAsync(null, CancellationToken.None);
        await Carts.UpsertItemAsync(cart.Id, widget.Id, 1, Now, CancellationToken.None);

        await Carts.RemoveItemAsync(cart.Id, widget.Id, CancellationToken.None);
        await Carts.TouchAsync(cart.Id, Now.AddHours(2), CancellationToken.None);

        var stored = await Carts.GetAsync(cart.Id, CancellationToken.None);
        Assert.Empty(stored!.Items);
        Assert.Equal(Now.AddHours(2), stored.UpdatedAt);
    }

    [Fact]
    public async Task Deleting_a_cart_takes_its_items_with_it()
    {
        var widget = await GivenWidget();
        var cart = await Carts.CreateAsync(null, CancellationToken.None);
        await Carts.UpsertItemAsync(cart.Id, widget.Id, 1, Now, CancellationToken.None);

        await Carts.DeleteAsync(cart.Id, CancellationToken.None);

        // Relies on the cascade; an orphaned cart_items row would violate the schema.
        Assert.Null(await Carts.GetAsync(cart.Id, CancellationToken.None));
    }

    // ---- users ---------------------------------------------------------------------------

    [Fact]
    public async Task A_user_is_found_by_normalized_email_regardless_of_typed_case()
    {
        var user = await GivenUser();

        var found = await Users.GetByNormalizedEmailAsync(user.Email.ToUpperInvariant(), CancellationToken.None);

        Assert.Equal(user.Id, found!.Id);
        Assert.Null(await Users.GetByNormalizedEmailAsync("NOBODY@EXAMPLE.COM", CancellationToken.None));
    }

    [Fact]
    public async Task A_google_user_is_found_by_subject()
    {
        var user = await GivenUser();
        user.GoogleSub = Unique("google-sub-");
        await Users.UpdateAsync(user, CancellationToken.None);

        var found = await Users.GetByGoogleSubAsync(user.GoogleSub, CancellationToken.None);

        Assert.Equal(user.Id, found!.Id);
        Assert.Null(await Users.GetByGoogleSubAsync("not-a-subject", CancellationToken.None));
    }

    [Fact]
    public async Task Lockout_state_and_the_security_stamp_persist()
    {
        var user = await GivenUser();
        var rotated = Guid.NewGuid();
        user.FailedAccessCount = 4;
        user.LockedUntil = Now.AddMinutes(15);
        user.SecurityStamp = rotated;

        await Users.UpdateAsync(user, CancellationToken.None);

        var stored = await Users.GetByIdAsync(user.Id, CancellationToken.None);
        Assert.Equal(4, stored!.FailedAccessCount);
        Assert.Equal(Now.AddMinutes(15), stored.LockedUntil);
        Assert.True(stored.IsLockedOut(Now));

        // The stamp is read on every request, so it has its own narrow query.
        Assert.Equal(rotated, await Users.GetSecurityStampAsync(user.Id, CancellationToken.None));
        Assert.Null(await Users.GetSecurityStampAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Two_users_cannot_share_an_email()
    {
        var user = await GivenUser();

        await Assert.ThrowsAnyAsync<Exception>(() => Users.AddAsync(
            new User
            {
                Id = Guid.NewGuid(),
                Email = user.Email,
                NormalizedEmail = user.NormalizedEmail,
                PasswordHash = "hash",
                Role = UserRoles.Customer,
                SecurityStamp = Guid.NewGuid(),
                CreatedAt = Now,
            },
            CancellationToken.None));
    }

    // ---- refresh tokens ------------------------------------------------------------------

    private RefreshTokenRepository RefreshTokens => new(db.Connections);

    private static RefreshToken TokenFor(Guid userId, Guid familyId, string hash) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = hash,
        FamilyId = familyId,
        ExpiresAt = Now.AddDays(14),
        CreatedAt = Now,
    };

    [Fact]
    public async Task A_refresh_token_round_trips_and_is_found_by_hash()
    {
        var user = await GivenUser();
        var token = TokenFor(user.Id, Guid.NewGuid(), Unique("hash-"));
        await RefreshTokens.AddAsync(token, CancellationToken.None);

        var stored = await RefreshTokens.GetByHashAsync(token.TokenHash, CancellationToken.None);

        Assert.Equal(token.Id, stored!.Id);
        Assert.Equal(token.FamilyId, stored.FamilyId);
        Assert.True(stored.IsActive(Now));
        Assert.Null(await RefreshTokens.GetByHashAsync("no-such-hash", CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_a_family_kills_every_token_in_it_and_spares_the_others()
    {
        var user = await GivenUser();
        var doomed = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var a = TokenFor(user.Id, doomed, Unique("hash-"));
        var b = TokenFor(user.Id, doomed, Unique("hash-"));
        var c = TokenFor(user.Id, untouched, Unique("hash-"));
        foreach (var t in new[] { a, b, c })
        {
            await RefreshTokens.AddAsync(t, CancellationToken.None);
        }

        await RefreshTokens.RevokeFamilyAsync(doomed, Now, CancellationToken.None);

        Assert.NotNull((await RefreshTokens.GetByHashAsync(a.TokenHash, CancellationToken.None))!.RevokedAt);
        Assert.NotNull((await RefreshTokens.GetByHashAsync(b.TokenHash, CancellationToken.None))!.RevokedAt);
        Assert.Null((await RefreshTokens.GetByHashAsync(c.TokenHash, CancellationToken.None))!.RevokedAt);
    }

    [Fact]
    public async Task Revoking_everything_for_a_user_signs_out_all_their_devices()
    {
        var user = await GivenUser();
        var other = await GivenUser();
        var mine = TokenFor(user.Id, Guid.NewGuid(), Unique("hash-"));
        var theirs = TokenFor(other.Id, Guid.NewGuid(), Unique("hash-"));
        await RefreshTokens.AddAsync(mine, CancellationToken.None);
        await RefreshTokens.AddAsync(theirs, CancellationToken.None);

        await RefreshTokens.RevokeAllForUserAsync(user.Id, Now, CancellationToken.None);

        Assert.NotNull((await RefreshTokens.GetByHashAsync(mine.TokenHash, CancellationToken.None))!.RevokedAt);
        Assert.Null((await RefreshTokens.GetByHashAsync(theirs.TokenHash, CancellationToken.None))!.RevokedAt);
    }

    [Fact]
    public async Task Rotation_records_what_replaced_a_token()
    {
        var user = await GivenUser();
        var family = Guid.NewGuid();
        var original = TokenFor(user.Id, family, Unique("hash-"));
        var replacement = TokenFor(user.Id, family, Unique("hash-"));
        await RefreshTokens.AddAsync(original, CancellationToken.None);
        await RefreshTokens.AddAsync(replacement, CancellationToken.None);

        original.RevokedAt = Now;
        original.ReplacedBy = replacement.Id;
        await RefreshTokens.UpdateAsync(original, CancellationToken.None);

        var stored = await RefreshTokens.GetByHashAsync(original.TokenHash, CancellationToken.None);
        Assert.Equal(Now, stored!.RevokedAt);
        Assert.Equal(replacement.Id, stored.ReplacedBy);
        Assert.False(stored.IsActive(Now));
    }

    // ---- two-factor ----------------------------------------------------------------------

    private TwoFactorRepository TwoFactor => new(db.Connections, TimeProvider.System);

    [Fact]
    public async Task A_pending_secret_becomes_confirmed_and_can_be_deleted()
    {
        var user = await GivenUser();

        await TwoFactor.UpsertPendingSecretAsync(user.Id, "SECRETBASE32", CancellationToken.None);
        Assert.False((await TwoFactor.GetSecretAsync(user.Id, CancellationToken.None))!.IsConfirmed);

        await TwoFactor.MarkConfirmedAsync(user.Id, CancellationToken.None);
        Assert.True((await TwoFactor.GetSecretAsync(user.Id, CancellationToken.None))!.IsConfirmed);

        await TwoFactor.DeleteSecretAsync(user.Id, CancellationToken.None);
        Assert.Null(await TwoFactor.GetSecretAsync(user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Re_enrolling_replaces_the_pending_secret_rather_than_adding_a_second()
    {
        var user = await GivenUser();

        await TwoFactor.UpsertPendingSecretAsync(user.Id, "FIRST", CancellationToken.None);
        await TwoFactor.UpsertPendingSecretAsync(user.Id, "SECOND", CancellationToken.None);

        Assert.Equal("SECOND", (await TwoFactor.GetSecretAsync(user.Id, CancellationToken.None))!.Secret);
    }

    [Fact]
    public async Task A_recovery_code_can_be_consumed_exactly_once()
    {
        var user = await GivenUser();
        await TwoFactor.AddRecoveryCodesAsync(user.Id, ["rc:one", "rc:two"], Now, CancellationToken.None);

        Assert.True(await TwoFactor.ConsumeRecoveryCodeAsync(user.Id, "rc:one", Now, CancellationToken.None));
        Assert.False(await TwoFactor.ConsumeRecoveryCodeAsync(user.Id, "rc:one", Now, CancellationToken.None));
        Assert.True(await TwoFactor.ConsumeRecoveryCodeAsync(user.Id, "rc:two", Now, CancellationToken.None));
    }

    [Fact]
    public async Task One_users_recovery_code_is_useless_to_another()
    {
        var owner = await GivenUser();
        var attacker = await GivenUser();
        await TwoFactor.AddRecoveryCodesAsync(owner.Id, ["rc:shared-value"], Now, CancellationToken.None);

        Assert.False(await TwoFactor.ConsumeRecoveryCodeAsync(attacker.Id, "rc:shared-value", Now, CancellationToken.None));
        Assert.True(await TwoFactor.ConsumeRecoveryCodeAsync(owner.Id, "rc:shared-value", Now, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_recovery_codes_clears_them_all()
    {
        var user = await GivenUser();
        await TwoFactor.AddRecoveryCodesAsync(user.Id, ["rc:a", "rc:b"], Now, CancellationToken.None);

        await TwoFactor.DeleteRecoveryCodesAsync(user.Id, CancellationToken.None);

        Assert.False(await TwoFactor.ConsumeRecoveryCodeAsync(user.Id, "rc:a", Now, CancellationToken.None));
    }

    // ---- password reset ------------------------------------------------------------------

    private PasswordResetTokenRepository ResetTokens => new(db.Connections);

    private static PasswordResetToken ResetFor(Guid userId, string hash) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = hash,
        ExpiresAt = Now.AddHours(1),
        CreatedAt = Now,
    };

    [Fact]
    public async Task A_reset_token_round_trips_and_can_be_marked_used()
    {
        var user = await GivenUser();
        var token = ResetFor(user.Id, Unique("reset-"));
        await ResetTokens.AddAsync(token, CancellationToken.None);

        var stored = await ResetTokens.GetByHashAsync(token.TokenHash, CancellationToken.None);
        Assert.True(stored!.IsActive(Now));

        await ResetTokens.MarkUsedAsync(token.Id, Now, CancellationToken.None);

        var used = await ResetTokens.GetByHashAsync(token.TokenHash, CancellationToken.None);
        Assert.Equal(Now, used!.UsedAt);
        Assert.False(used.IsActive(Now));
    }

    [Fact]
    public async Task Requesting_a_new_reset_invalidates_the_outstanding_ones()
    {
        var user = await GivenUser();
        var first = ResetFor(user.Id, Unique("reset-"));
        var second = ResetFor(user.Id, Unique("reset-"));
        await ResetTokens.AddAsync(first, CancellationToken.None);

        await ResetTokens.InvalidateForUserAsync(user.Id, Now, CancellationToken.None);
        await ResetTokens.AddAsync(second, CancellationToken.None);

        // Only the newest link may work, or an old email stays a way in.
        Assert.False((await ResetTokens.GetByHashAsync(first.TokenHash, CancellationToken.None))!.IsActive(Now));
        Assert.True((await ResetTokens.GetByHashAsync(second.TokenHash, CancellationToken.None))!.IsActive(Now));
    }

    [Fact]
    public async Task An_unknown_reset_hash_returns_null()
    {
        Assert.Null(await ResetTokens.GetByHashAsync("never-issued", CancellationToken.None));
    }

    // ---- audit log -----------------------------------------------------------------------

    [Fact]
    public async Task Audit_entries_are_written_for_a_user_and_anonymously()
    {
        var user = await GivenUser();
        var audit = new AuditLog(db.Connections, TimeProvider.System);

        await audit.WriteAsync(user.Id, "test.action", "detail", CancellationToken.None);
        await audit.WriteAsync(null, "test.anonymous", null, CancellationToken.None);

        // No read side on the port; the assertion is that neither write throws or violates the FK.
        Assert.NotNull(await Users.GetByIdAsync(user.Id, CancellationToken.None));
    }

    // ---- category narrowing and ordering, moved here from the browser -------------------------
    // These replace the refine() unit tests: the behaviour is SQL now, so this is where it is
    // proven. Every case scopes itself with a unique token so a shared database stays usable.

    [Fact]
    public async Task A_category_narrows_the_listing_to_its_members()
    {
        var token = Unique("cat");
        await GivenWidget(name: $"Mega Widget Block {token}");
        await GivenWidget(name: $"Mega Widget Hub {token}");
        await GivenWidget(name: $"Mini Widget Block {token}");

        var query = new WidgetQuery($"{token}", ActiveOnly: true, 1, 50, Category: "mega");
        var results = await Widgets.SearchAsync(query, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, w => Assert.Contains("Mega", w.Name, StringComparison.Ordinal));
        // The count has to agree with the page, or the storefront reports a total it never shows.
        Assert.Equal(2, await Widgets.CountAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task A_search_and_a_category_narrow_together_rather_than_either_or()
    {
        var token = Unique("both");
        await GivenWidget(name: $"Mega Widget Turbine {token}");
        await GivenWidget(name: $"Mega Widget Block {token}");
        await GivenWidget(name: $"Mini Widget Turbine {token}");

        var results = await Widgets.SearchAsync(
            new WidgetQuery($"Turbine {token}", ActiveOnly: true, 1, 50, Category: "mega"),
            CancellationToken.None);

        // "Turbine" within Mega means both conditions, not their union.
        Assert.Single(results);
        Assert.Contains("Mega Widget Turbine", results[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_category_leaves_the_listing_alone()
    {
        var token = Unique("nocat");
        await GivenWidget(name: $"Mega Widget {token}");
        await GivenWidget(name: $"Mini Widget {token}");

        var results = await Widgets.SearchAsync(
            new WidgetQuery(token, ActiveOnly: true, 1, 50, Category: null),
            CancellationToken.None);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Price_sorts_run_in_both_directions()
    {
        var token = Unique("price");
        await GivenWidget(name: $"B {token}", price: 30m);
        await GivenWidget(name: $"A {token}", price: 10m);
        await GivenWidget(name: $"C {token}", price: 20m);

        var ascending = await Widgets.SearchAsync(
            new WidgetQuery(token, ActiveOnly: true, 1, 50, Sort: WidgetSort.PriceAscending), CancellationToken.None);
        var descending = await Widgets.SearchAsync(
            new WidgetQuery(token, ActiveOnly: true, 1, 50, Sort: WidgetSort.PriceDescending), CancellationToken.None);

        Assert.Equal([10m, 20m, 30m], ascending.Select(w => w.Price));
        Assert.Equal([30m, 20m, 10m], descending.Select(w => w.Price));
    }

    [Fact]
    public async Task Featured_leads_with_what_can_actually_be_bought()
    {
        var token = Unique("feat");
        await GivenWidget(name: $"A sold out {token}", onHand: 5, reserved: 5);
        await GivenWidget(name: $"B in stock {token}", onHand: 5);

        var results = await Widgets.SearchAsync(
            new WidgetQuery(token, ActiveOnly: true, 1, 50, Sort: WidgetSort.Featured), CancellationToken.None);

        // Alphabetically the sold-out one comes first; availability outranks the name.
        Assert.Contains("in stock", results[0].Name, StringComparison.Ordinal);
        Assert.Contains("sold out", results[1].Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_sort_falls_back_instead_of_reaching_the_database()
    {
        var token = Unique("inject");
        await GivenWidget(name: $"A {token}");
        await GivenWidget(name: $"B {token}");

        // The value is mapped through a fixed set, never interpolated, so even this is inert.
        var results = await Widgets.SearchAsync(
            new WidgetQuery(token, ActiveOnly: true, 1, 50, Sort: "price; drop table widgets"),
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains($"A {token}", results[0].Name, StringComparison.Ordinal);
    }
}
