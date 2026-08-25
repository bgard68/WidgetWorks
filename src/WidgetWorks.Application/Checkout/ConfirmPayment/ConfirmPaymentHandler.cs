using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Checkout.ConfirmPayment;

public sealed record ConfirmPaymentCommand(string Provider, PaymentEventType Type, string Reference);

/// <summary>
/// Settles an order parked in AwaitingPayment from a provider webhook event. Idempotent: a duplicate
/// delivery, or an event for an order that has already moved on, is a no-op that returns the current
/// status. On success it marks the order Paid and emails the receipt; on failure it releases the
/// inventory reservation.
/// </summary>
public sealed class ConfirmPaymentHandler(
    IOrderRepository orders,
    IEmailSender email,
    TimeProvider clock,
    ILogger<ConfirmPaymentHandler> logger)
{
    public async Task<Result<string>> Handle(ConfirmPaymentCommand command, CancellationToken ct)
    {
        var order = await orders.GetByPaymentReferenceAsync(command.Provider, command.Reference, ct);
        if (order is null)
        {
            return Result<string>.Fail("No order matches this payment reference.");
        }

        // Only an order still awaiting payment can transition; anything else is treated as already-handled.
        if (order.Status != OrderStatus.AwaitingPayment)
        {
            return Result<string>.Success(order.Status);
        }

        var now = clock.GetUtcNow();

        if (command.Type == PaymentEventType.Succeeded)
        {
            // The status check above is a courtesy; this is the decision. Two deliveries of the
            // same event both pass that check, and the database picks exactly one winner. Only the
            // winner sends a receipt, so a retried webhook cannot email the customer twice.
            if (!await orders.MarkPaidAsync(order.Id, order.PaymentProvider ?? command.Provider, command.Reference, now, ct))
            {
                return Result<string>.Success(order.Status);
            }

            order.Status = OrderStatus.Paid;

            try
            {
                await email.SendAsync(EmailTemplates.OrderReceived(order), ct);
            }
            catch (Exception ex)
            {
                // The provider settled the money; failing here would make it retry a
                // webhook that worked. Logged so the missing receipt is traceable.
                logger.LogWarning(
                    ex,
                    "Receipt email failed for settled order {OrderNumber}; the order is paid.",
                    order.OrderNumber);
            }

            return Result<string>.Success(OrderStatus.Paid);
        }

        // Same contract on the failure path, and it matters more here: the losing caller must not
        // release the reservation a second time.
        if (!await orders.MarkPaymentFailedAsync(order, "Payment failed.", now, ct))
        {
            return Result<string>.Success(order.Status);
        }

        order.Status = OrderStatus.PaymentFailed;
        return Result<string>.Success(OrderStatus.PaymentFailed);
    }
}
