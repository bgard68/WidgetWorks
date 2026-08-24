using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Carts.UpdateItem;
using WidgetWorks.Application.Catalog;
using WidgetWorks.Domain.Carts;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Infrastructure.Security;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The small pieces that are easy to leave untested and expensive to get wrong: the two token
/// generators (where "random" and "one-way" are the whole contract), the catalog read model, and
/// the cart quantity rules.
/// </summary>
public class SmallServicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- recovery codes ------------------------------------------------------------------

    [Fact]
    public void Recovery_codes_are_unique_lowercase_hex_and_stored_only_as_hashes()
    {
        var codes = new RecoveryCodeService().Generate(10);

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Select(c => c.Plain).Distinct().Count());
        Assert.All(codes, c =>
        {
            Assert.Equal(10, c.Plain.Length);                     // 5 bytes as hex
            Assert.Equal(c.Plain.ToLowerInvariant(), c.Plain);    // typed by a human under stress
            Assert.NotEqual(c.Plain, c.Hash);                     // never stored in the clear
        });
    }

    [Fact]
    public void Recovery_code_hashing_is_deterministic_and_case_sensitive()
    {
        var service = new RecoveryCodeService();

        Assert.Equal(service.Hash("abc123"), service.Hash("abc123"));
        Assert.NotEqual(service.Hash("abc123"), service.Hash("abc124"));

        // The login handler lowercases before hashing; the hash itself must not do it silently.
        Assert.NotEqual(service.Hash("ABC123"), service.Hash("abc123"));
    }

    [Fact]
    public void Generating_zero_codes_is_allowed_and_yields_nothing()
    {
        Assert.Empty(new RecoveryCodeService().Generate(0));
    }

    // ---- opaque tokens -------------------------------------------------------------------

    [Fact]
    public void Secure_tokens_are_url_safe_and_never_repeat()
    {
        var generator = new SecureTokenGenerator();

        var tokens = Enumerable.Range(0, 50).Select(_ => generator.Generate()).ToList();

        Assert.Equal(50, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            // base64url: it ends up in a reset link, so + / = would need escaping.
            Assert.DoesNotContain('+', t);
            Assert.DoesNotContain('/', t);
            Assert.DoesNotContain('=', t);
            Assert.True(t.Length >= 42);
        });
    }

    [Fact]
    public void Secure_token_hashing_is_deterministic_and_one_way()
    {
        var generator = new SecureTokenGenerator();
        var raw = generator.Generate();

        var hash = generator.Hash(raw);

        Assert.Equal(hash, generator.Hash(raw));
        Assert.NotEqual(raw, hash);
        Assert.Equal(64, hash.Length);                        // SHA-256 as hex
        Assert.NotEqual(hash, generator.Hash(raw + "x"));
    }

    // ---- catalog read model --------------------------------------------------------------

    [Fact]
    public void The_widget_view_reports_availability_net_of_reservations()
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = "WW-001",
            Name = "Standard Widget",
            Description = "Dependable.",
            ImageUrl = null,
            Price = 9.99m,
            IsActive = true,
            QuantityOnHand = 10,
            QuantityReserved = 4,
        };

        var view = WidgetView.From(widget);

        Assert.Equal(widget.Id, view.Id);
        Assert.Equal("WW-001", view.Sku);
        Assert.Equal("Standard Widget", view.Name);
        Assert.Equal("Dependable.", view.Description);
        Assert.Null(view.ImageUrl);
        Assert.Equal(9.99m, view.Price);
        Assert.True(view.IsActive);
        Assert.Equal(10, view.QuantityOnHand);
        Assert.Equal(4, view.QuantityReserved);

        // What a shopper can actually buy — reserved stock belongs to someone else's order.
        Assert.Equal(6, view.QuantityAvailable);
    }

    [Fact]
    public void Availability_never_goes_negative_even_if_reservations_exceed_stock()
    {
        var widget = new Widget { Id = Guid.NewGuid(), QuantityOnHand = 2, QuantityReserved = 5 };

        Assert.Equal(0, WidgetView.From(widget).QuantityAvailable);
    }

    // ---- cart quantity rules -------------------------------------------------------------

    private sealed record Ctx(InMemoryCartRepository Carts, InMemoryWidgetRepository Widgets, Cart Cart, Widget Widget);

    private static Ctx Setup(int available = 5, bool active = true)
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = "WW-001",
            Name = "Standard Widget",
            Price = 10m,
            QuantityOnHand = available,
            IsActive = active,
        };
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = new Cart { Id = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        cart.Items.Add(new CartItem { CartId = cart.Id, WidgetId = widget.Id, Quantity = 2 });
        carts.Store[cart.Id] = cart;

        return new Ctx(carts, widgets, cart, widget);
    }

    private static UpdateCartItemHandler Handler(Ctx c) => new(c.Carts, c.Widgets, new FakeTimeProvider(Now));

    [Fact]
    public async Task Setting_a_quantity_of_zero_removes_the_line()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new UpdateCartItemCommand(c.Cart.Id, c.Widget.Id, 0), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
    }

    [Fact]
    public async Task A_negative_quantity_removes_the_line_rather_than_erroring()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new UpdateCartItemCommand(c.Cart.Id, c.Widget.Id, -3), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
    }

    [Fact]
    public async Task Asking_for_more_than_exists_caps_at_what_is_available()
    {
        var c = Setup(available: 5);

        var result = await Handler(c).Handle(new UpdateCartItemCommand(c.Cart.Id, c.Widget.Id, 99), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value!.Items.Single().Quantity);
    }

    [Fact]
    public async Task An_out_of_stock_widget_is_refused_with_a_reason()
    {
        var c = Setup(available: 0);

        var result = await Handler(c).Handle(new UpdateCartItemCommand(c.Cart.Id, c.Widget.Id, 1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("This widget is out of stock.", result.Error);
    }

    [Fact]
    public async Task A_hidden_widget_cannot_be_added_to_a_cart()
    {
        var c = Setup(active: false);

        var result = await Handler(c).Handle(new UpdateCartItemCommand(c.Cart.Id, c.Widget.Id, 1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Widget not found.", result.Error);
    }

    [Fact]
    public async Task An_unknown_widget_is_refused()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new UpdateCartItemCommand(c.Cart.Id, Guid.NewGuid(), 1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Widget not found.", result.Error);
    }

    [Fact]
    public async Task An_unknown_cart_is_refused()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new UpdateCartItemCommand(Guid.NewGuid(), c.Widget.Id, 1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Cart not found.", result.Error);
    }

    [Fact]
    public async Task Updating_a_quantity_touches_the_cart()
    {
        var c = Setup();

        await Handler(c).Handle(new UpdateCartItemCommand(c.Cart.Id, c.Widget.Id, 3), CancellationToken.None);

        Assert.Equal(Now, c.Carts.Store[c.Cart.Id].UpdatedAt);
    }

    // ---- dapper mapping ------------------------------------------------------------------

    [Fact]
    public void Dapper_configuration_applies_exactly_once()
    {
        // Called from AddInfrastructure and from every test fixture; the second call must be a
        // no-op rather than re-registering mappings.
        WidgetWorks.Infrastructure.Persistence.DapperConfiguration.Apply();
        WidgetWorks.Infrastructure.Persistence.DapperConfiguration.Apply();

        Assert.True(Dapper.DefaultTypeMap.MatchNamesWithUnderscores);
    }
}
