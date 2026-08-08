using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Infrastructure.Payments;
using WidgetWorks.Infrastructure.Pricing;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class CheckoutTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static ShippingAddressInput Address(string state = "CA")
        => new("Jane Doe", "1 Main St", null, "Springfield", state, "90001", "US");

    private sealed record Ctx(InMemoryCartRepository Carts, InMemoryWidgetRepository Widgets, InMemoryOrderRepository Orders, Widget Widget, Guid CartId);

    private static async Task<Ctx> SetupAsync(int onHand = 10, decimal price = 10m, int qty = 2)
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", IsActive = true, Price = price, QuantityOnHand = onHand };
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, widget.Id, qty, default, CancellationToken.None);

        var orders = new InMemoryOrderRepository(widgets);
        return new Ctx(carts, widgets, orders, widget, cart.Id);
    }

    private static CheckoutHandler Handler(Ctx c, MockPaymentGateway gateway, FakeEmailSender email)
        => new(c.Carts, c.Widgets, c.Orders, new FlatRateShippingCalculator(),
            new StateSalesTaxCalculator(new StaticStateTaxRateProvider()), gateway, email, Clock());

    [Fact]
    public async Task Successful_checkout_pays_reserves_clears_cart_and_emails_receipt()
    {
        var c = await SetupAsync();
        var email = new FakeEmailSender();
        var result = await Handler(c, new MockPaymentGateway(), email)
            .Handle(new CheckoutCommand(c.CartId, null, "jane@example.com", Address(), "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Paid, result.Value!.Status);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
        Assert.Null(await c.Carts.GetAsync(c.CartId, CancellationToken.None));
        Assert.Equal(29.19m, result.Value!.Total);   // 20 + 7.74 shipping + 1.45 tax
        Assert.Contains(email.Sent, m => m.Subject.Contains("received"));
    }

    [Fact]
    public async Task Declined_payment_releases_reservation_and_keeps_cart()
    {
        var c = await SetupAsync();
        var result = await Handler(c, new MockPaymentGateway(), new FakeEmailSender())
            .Handle(new CheckoutCommand(c.CartId, null, "jane@example.com", Address(), "Standard", "decline"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(0, c.Widgets.Store[c.Widget.Id].QuantityReserved);
        Assert.NotNull(await c.Carts.GetAsync(c.CartId, CancellationToken.None));
        Assert.Single(c.Orders.Orders);
        Assert.Equal(OrderStatus.PaymentFailed, c.Orders.Orders[0].Status);
    }

    [Fact]
    public async Task Checkout_fails_when_stock_insufficient()
    {
        var c = await SetupAsync(onHand: 5, qty: 5);
        c.Widgets.Store[c.Widget.Id].QuantityReserved = 3;   // only 2 available now
        var result = await Handler(c, new MockPaymentGateway(), new FakeEmailSender())
            .Handle(new CheckoutCommand(c.CartId, null, "jane@example.com", Address(), "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(c.Orders.Orders);
    }

    [Fact]
    public async Task Checkout_requires_valid_email()
    {
        var c = await SetupAsync();
        var result = await Handler(c, new MockPaymentGateway(), new FakeEmailSender())
            .Handle(new CheckoutCommand(c.CartId, null, "not-an-email", Address(), "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Empty_cart_cannot_checkout()
    {
        var widgets = new InMemoryWidgetRepository();
        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        var orders = new InMemoryOrderRepository(widgets);
        var handler = new CheckoutHandler(carts, widgets, orders, new FlatRateShippingCalculator(),
            new StateSalesTaxCalculator(new StaticStateTaxRateProvider()), new MockPaymentGateway(), new FakeEmailSender(), Clock());

        var result = await handler.Handle(new CheckoutCommand(cart.Id, null, "jane@example.com", Address(), "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }
}
