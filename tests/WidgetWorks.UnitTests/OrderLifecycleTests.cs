using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Orders.UpdateStatus;
using WidgetWorks.Domain.Orders;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class OrderLifecycleTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static (InMemoryOrderRepository Orders, FakeEmailSender Email, Order Order) Setup(string status = OrderStatus.Paid)
    {
        var widgets = new InMemoryWidgetRepository();
        var orders = new InMemoryOrderRepository(widgets);
        var order = new Order { Id = Guid.NewGuid(), OrderNumber = "WW-1", Email = "jane@example.com", Status = status, Total = 10m };
        order.Items.Add(new OrderItem { Id = Guid.NewGuid(), WidgetId = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", UnitPrice = 10m, Quantity = 1, LineSubtotal = 10m });
        orders.Orders.Add(order);
        return (orders, new FakeEmailSender(), order);
    }

    [Fact]
    public async Task Paid_to_shipped_sets_tracking_and_emails()
    {
        var (orders, email, order) = Setup();
        var handler = new UpdateOrderStatusHandler(orders, email, Clock(), NullLogger<UpdateOrderStatusHandler>.Instance);

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Shipped, "1Z999"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Shipped, result.Value!.Status);
        Assert.Equal("1Z999", result.Value!.TrackingNumber);
        Assert.Contains(email.Sent, m => m.Subject.Contains("shipped"));
    }

    [Fact]
    public async Task Cannot_deliver_before_shipping()
    {
        var (orders, email, order) = Setup();
        var handler = new UpdateOrderStatusHandler(orders, email, Clock(), NullLogger<UpdateOrderStatusHandler>.Instance);

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Delivered, null), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Shipped_to_delivered_is_allowed()
    {
        var (orders, email, order) = Setup(OrderStatus.Shipped);
        var handler = new UpdateOrderStatusHandler(orders, email, Clock(), NullLogger<UpdateOrderStatusHandler>.Instance);

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Delivered, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Delivered, result.Value!.Status);
    }

    [Fact]
    public async Task Cancel_from_paid_is_allowed_and_emails()
    {
        var (orders, email, order) = Setup();
        var handler = new UpdateOrderStatusHandler(orders, email, Clock(), NullLogger<UpdateOrderStatusHandler>.Instance);

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Cancelled, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(email.Sent, m => m.Subject.Contains("cancelled"));
    }

    [Fact]
    public async Task An_unknown_order_cannot_change_status()
    {
        var (orders, email, _) = Setup();
        var handler = new UpdateOrderStatusHandler(orders, email, Clock(), NullLogger<UpdateOrderStatusHandler>.Instance);

        var result = await handler.Handle(new UpdateOrderStatusCommand(Guid.NewGuid(), OrderStatus.Shipped, null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Order not found.", result.Error);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task A_failed_shipping_email_does_not_undo_the_transition()
    {
        var (orders, _, order) = Setup();
        var handler = new UpdateOrderStatusHandler(orders, new ThrowingEmailSender(), Clock(), NullLogger<UpdateOrderStatusHandler>.Instance);

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Shipped, "1Z999"), CancellationToken.None);

        // The parcel left the warehouse whether or not the mail server was up.
        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Shipped, orders.Orders.Single().Status);
        Assert.Equal("1Z999", orders.Orders.Single().TrackingNumber);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct)
            => throw new InvalidOperationException("smtp is down");
    }
}
