using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Checkout.Quote;
using WidgetWorks.Application.Pricing;
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

    private static CheckoutHandler Handler(Ctx c, MockPaymentGateway gateway, IEmailSender email)
        => new(c.Carts, c.Widgets, c.Orders, new OrderPricer(new FlatRateShippingCalculator(), new StateSalesTaxCalculator(new StaticStateTaxRateProvider())), gateway, email, Clock());

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

        // The whole receipt payload, not just the status: the SPA renders every one of these.
        var placed = c.Orders.Orders.Single();
        Assert.Equal(placed.OrderNumber, result.Value!.OrderNumber);
        Assert.Equal(placed.Id, result.Value!.OrderId);
        Assert.Equal("Mock", result.Value!.PaymentProvider);
        Assert.False(string.IsNullOrEmpty(result.Value!.PaymentReference));
        Assert.Null(result.Value!.ClientSecret);       // synchronous payment needs no client action
        Assert.Null(result.Value!.NextActionUrl);
        Assert.Contains(email.Sent, m => m.Subject.Contains("received"));
    }

    [Fact]
    public async Task Async_payment_parks_order_awaiting_payment_reserves_and_clears_cart()
    {
        var c = await SetupAsync();
        var email = new FakeEmailSender();
        var result = await Handler(c, new MockPaymentGateway(), email)
            .Handle(new CheckoutCommand(c.CartId, null, "jane@example.com", Address(), "Standard", "klarna_demo"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.AwaitingPayment, result.Value!.Status);
        Assert.False(string.IsNullOrEmpty(result.Value!.PaymentReference));   // reference persisted for the webhook
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);       // reservation held
        Assert.Null(await c.Carts.GetAsync(c.CartId, CancellationToken.None)); // cart cleared
        Assert.Empty(email.Sent);                                             // no receipt until settled
        Assert.Single(c.Orders.Orders);
        Assert.Equal(OrderStatus.AwaitingPayment, c.Orders.Orders[0].Status);
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
        var handler = new CheckoutHandler(carts, widgets, orders, new OrderPricer(new FlatRateShippingCalculator(), new StateSalesTaxCalculator(new StaticStateTaxRateProvider())), new MockPaymentGateway(), new FakeEmailSender(), Clock());

        var result = await handler.Handle(new CheckoutCommand(cart.Id, null, "jane@example.com", Address(), "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData("", "Springfield", "CA", "90001")]     // no street
    [InlineData("1 Main St", "", "CA", "90001")]       // no city
    [InlineData("1 Main St", "Springfield", "", "90001")]  // no state
    [InlineData("1 Main St", "Springfield", "CA", "")] // no postal code
    public async Task An_incomplete_shipping_address_is_refused(string line1, string city, string state, string postal)
    {
        var c = await SetupAsync();
        var address = new ShippingAddressInput("Jane Doe", line1, null, city, state, postal, "US");

        var result = await Handler(c, new MockPaymentGateway(), new FakeEmailSender())
            .Handle(new CheckoutCommand(c.CartId, null, "jane@example.com", address, "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("A complete shipping address is required.", result.Error);
        Assert.Empty(c.Orders.Orders);
    }

    [Fact]
    public async Task Checkout_of_an_unknown_cart_fails()
    {
        var c = await SetupAsync();

        var result = await Handler(c, new MockPaymentGateway(), new FakeEmailSender())
            .Handle(new CheckoutCommand(Guid.NewGuid(), null, "jane@example.com", Address(), "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart not found.", result.Error);
    }

    [Fact]
    public async Task A_failed_receipt_email_does_not_fail_a_paid_order()
    {
        var c = await SetupAsync();

        var result = await Handler(c, new MockPaymentGateway(), new ThrowingEmailSender())
            .Handle(new CheckoutCommand(c.CartId, null, "jane@example.com", Address(), "Standard", "tok_ok"), CancellationToken.None);

        // The money moved; a dead mail server must not turn that into a checkout error.
        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Paid, result.Value!.Status);
        Assert.Equal(OrderStatus.Paid, c.Orders.Orders.Single().Status);
    }

    [Fact]
    public async Task A_quote_prices_the_cart_without_placing_anything()
    {
        var c = await SetupAsync();
        var quote = new QuoteCartHandler(c.Carts, c.Widgets, new OrderPricer(new FlatRateShippingCalculator(), new StateSalesTaxCalculator(new StaticStateTaxRateProvider())));

        var result = await quote.Handle(new QuoteCartCommand(c.CartId, "CA", "Standard"), CancellationToken.None);

        // The checkout page renders every field of the breakdown.
        Assert.True(result.IsSuccess);
        var view = result.Value!;
        Assert.Equal(20m, view.Subtotal);
        Assert.Equal("Standard", view.ShippingMethod);
        Assert.Equal(7.74m, view.Shipping);
        Assert.Equal("CA", view.StateCode);
        Assert.Equal(0.0725m, view.TaxRate);
        Assert.Equal(1.45m, view.Tax);
        Assert.Equal(29.19m, view.Total);
        Assert.Equal(2, view.ItemCount);
        Assert.False(view.IsEmpty);
        Assert.Empty(c.Orders.Orders);
        Assert.NotNull(await c.Carts.GetAsync(c.CartId, CancellationToken.None));
    }

    [Fact]
    public async Task A_quote_for_an_unknown_cart_fails()
    {
        var c = await SetupAsync();
        var quote = new QuoteCartHandler(c.Carts, c.Widgets, new OrderPricer(new FlatRateShippingCalculator(), new StateSalesTaxCalculator(new StaticStateTaxRateProvider())));

        var result = await quote.Handle(new QuoteCartCommand(Guid.NewGuid(), "CA", "Standard"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart not found.", result.Error);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct)
            => throw new InvalidOperationException("smtp is down");
    }
}
