using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Orders.UpdateStatus;

public sealed record UpdateOrderStatusCommand(Guid OrderId, string Status, string? TrackingNumber);

/// <summary>
/// Drives fulfilment. The legal transitions belong to the order itself (see
/// <see cref="Order.TransitionTo"/>); this handler asks permission, persists what the entity
/// decided, and notifies the customer.
/// </summary>
public sealed class UpdateOrderStatusHandler(
    IOrderRepository orders,
    IEmailSender email,
    TimeProvider clock,
    ILogger<UpdateOrderStatusHandler> logger)
{
    public async Task<Result<OrderView>> Handle(UpdateOrderStatusCommand command, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.OrderId, ct);
        if (order is null)
        {
            return Result<OrderView>.Fail("Order not found.");
        }

        var target = (command.Status ?? string.Empty).Trim();
        if (!order.CanTransitionTo(target))
        {
            // Asked, not caught: a refused transition is an expected outcome here, not an exception.
            return Result<OrderView>.Fail($"Cannot change status from {order.Status} to '{target}'.");
        }

        var now = clock.GetUtcNow();
        order.TransitionTo(target, command.TrackingNumber, now);
        await orders.UpdateStatusAsync(order.Id, order.Status, order.TrackingNumber, now, ct);

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
        catch (Exception ex)
        {
            // The parcel left the warehouse whether or not the mail server was up,
            // so the transition stands. Logged so the customer's missing notice is
            // explainable.
            //
            // The status is written as a domain constant chosen by comparison, not
            // as the caller's own string. command.Status arrives from the request
            // body, and a value carrying newlines could otherwise forge whole log
            // entries (CWE-117). Only these two branches send mail, so only these
            // two can reach this catch.
            logger.LogWarning(
                ex,
                "Status email failed for order {OrderNumber} moving to {Status}.",
                order.OrderNumber,
                target == OrderStatus.Shipped ? OrderStatus.Shipped : OrderStatus.Cancelled);
        }

        return Result<OrderView>.Success(OrderView.From(order));
    }
}
