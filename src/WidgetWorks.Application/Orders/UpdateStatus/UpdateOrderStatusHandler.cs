using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Orders.UpdateStatus;

public sealed record UpdateOrderStatusCommand(Guid OrderId, string Status, string? TrackingNumber);

public sealed class UpdateOrderStatusHandler(IOrderRepository orders, IEmailSender email, TimeProvider clock)
{
    // Allowed forward transitions. Anything not listed is rejected.
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        [OrderStatus.Paid] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
    };

    public async Task<Result<OrderView>> Handle(UpdateOrderStatusCommand command, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.OrderId, ct);
        if (order is null)
        {
            return Result<OrderView>.Fail("Order not found.");
        }

        var target = (command.Status ?? string.Empty).Trim();
        if (!Allowed.TryGetValue(order.Status, out var next) || Array.IndexOf(next, target) < 0)
        {
            return Result<OrderView>.Fail($"Cannot change status from {order.Status} to '{target}'.");
        }

        var now = clock.GetUtcNow();
        var tracking = string.IsNullOrWhiteSpace(command.TrackingNumber) ? order.TrackingNumber : command.TrackingNumber.Trim();
        await orders.UpdateStatusAsync(order.Id, target, tracking, now, ct);
        order.Status = target;
        order.TrackingNumber = tracking;
        order.UpdatedAt = now;

        try
        {
            if (target == OrderStatus.Shipped)
            {
                await email.SendAsync(EmailTemplates.OrderShipped(order), ct);
            }
            else if (target == OrderStatus.Cancelled)
            {
                await email.SendAsync(EmailTemplates.OrderCancelled(order), ct);
            }
        }
        catch
        {
            // Transactional email is best-effort; a notification failure must not fail the transition.
        }

        return Result<OrderView>.Success(OrderView.From(order));
    }
}
