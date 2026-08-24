using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.ConfirmPayment;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Infrastructure.Payments;
using WidgetWorks.Infrastructure.Pricing;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class ConfirmPaymentTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static ShippingAddressInput Address() => new("Jane Doe", "1 Main St", null, "Springfield", "CA", "90001", "US");

    private sealed record Ctx(InMemoryWidgetRepository Widgets, InMemoryOrderRepository Orders, Widget Widget);

    private static async Task<(Ctx Ctx, CheckoutResult Placed, FakeEmailSender Email)> PlaceAsyncOrder()
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", IsActive = true, Price = 10m, QuantityOnHand = 10 };
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, widget.Id, 2, default, CancellationToken.None);

        var orders = new InMemoryOrderRepository(widgets);
        var email = new FakeEmailSender();
        var handler = new CheckoutHandler(carts, widgets, orders, new OrderPricer(new FlatRateShippingCalculator(), new StateSalesTaxCalculator(new StaticStateTaxRateProvider())), new MockPaymentGateway(), email, Clock());

        var result = await handler.Handle(
            new CheckoutCommand(cart.Id, null, "jane@example.com", Address(), "Standard", "klarna_demo"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.AwaitingPayment, result.Value!.Status);
        return (new Ctx(widgets, orders, widget), result.Value!, email);
    }

    [Fact]
    public async Task Succeeded_webhook_marks_paid_and_emails_receipt()
    {
        var (ctx, placed, email) = await PlaceAsyncOrder();
        var confirm = new ConfirmPaymentHandler(ctx.Orders, email, Clock());

        var result = await confirm.Handle(
            new ConfirmPaymentCommand("Mock", PaymentEventType.Succeeded, placed.PaymentReference), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Paid, result.Value);
        Assert.Equal(OrderStatus.Paid, ctx.Orders.Orders[0].Status);
        Assert.Equal(2, ctx.Widgets.Store[ctx.Widget.Id].QuantityReserved);   // stock stays committed
        Assert.Contains(email.Sent, m => m.Subject.Contains("received"));
    }

    [Fact]
    public async Task A_failed_receipt_email_does_not_fail_the_settlement()
    {
        var (ctx, placed, _) = await PlaceAsyncOrder();
        var confirm = new ConfirmPaymentHandler(ctx.Orders, new ThrowingEmailSender(), Clock());

        var result = await confirm.Handle(
            new ConfirmPaymentCommand("Mock", PaymentEventType.Succeeded, placed.PaymentReference), CancellationToken.None);

        // The provider settled the money; a 500 here would make it retry a webhook that worked.
        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Paid, ctx.Orders.Orders[0].Status);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct)
            => throw new InvalidOperationException("smtp is down");
    }

    [Fact]
    public async Task Failed_webhook_marks_failed_and_releases_reservation()
    {
        var (ctx, placed, email) = await PlaceAsyncOrder();
        var confirm = new ConfirmPaymentHandler(ctx.Orders, email, Clock());

        var result = await confirm.Handle(
            new ConfirmPaymentCommand("Mock", PaymentEventType.Failed, placed.PaymentReference), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.PaymentFailed, result.Value);
        Assert.Equal(0, ctx.Widgets.Store[ctx.Widget.Id].QuantityReserved);   // reservation released
    }

    [Fact]
    public async Task Duplicate_webhook_is_idempotent()
    {
        var (ctx, placed, email) = await PlaceAsyncOrder();
        var confirm = new ConfirmPaymentHandler(ctx.Orders, email, Clock());
        await confirm.Handle(new ConfirmPaymentCommand("Mock", PaymentEventType.Succeeded, placed.PaymentReference), CancellationToken.None);

        // A late/duplicate "failed" event for an already-paid order is a no-op.
        var second = await confirm.Handle(
            new ConfirmPaymentCommand("Mock", PaymentEventType.Failed, placed.PaymentReference), CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(OrderStatus.Paid, second.Value);
        Assert.Equal(OrderStatus.Paid, ctx.Orders.Orders[0].Status);
        Assert.Single(email.Sent);   // receipt sent exactly once
    }

    [Fact]
    public async Task Unknown_reference_is_reported()
    {
        var (ctx, _, email) = await PlaceAsyncOrder();
        var confirm = new ConfirmPaymentHandler(ctx.Orders, email, Clock());

        var result = await confirm.Handle(
            new ConfirmPaymentCommand("Mock", PaymentEventType.Succeeded, "mock_pi_nope"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }
}
