using Microsoft.Extensions.Time.Testing;
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
        var handler = new UpdateOrderStatusHandler(orders, email, Clock());

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
        var handler = new UpdateOrderStatusHandler(orders, email, Clock());

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Delivered, null), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Shipped_to_delivered_is_allowed()
    {
        var (orders, email, order) = Setup(OrderStatus.Shipped);
        var handler = new UpdateOrderStatusHandler(orders, email, Clock());

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Delivered, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Delivered, result.Value!.Status);
    }

    [Fact]
    public async Task Cancel_from_paid_is_allowed_and_emails()
    {
        var (orders, email, order) = Setup();
        var handler = new UpdateOrderStatusHandler(orders, email, Clock());

        var result = await handler.Handle(new UpdateOrderStatusCommand(order.Id, OrderStatus.Cancelled, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(email.Sent, m => m.Subject.Contains("cancelled"));
    }
}
