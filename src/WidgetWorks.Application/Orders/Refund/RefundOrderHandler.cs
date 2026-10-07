using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Domain.Demo;

namespace WidgetWorks.Application.Orders.Refund;

/// <summary>
/// A refund request. <paramref name="Amount"/> is null for the whole remaining balance, which is the
/// common case; <paramref name="ActorId"/> is the staff member doing it, because "who refunded this"
/// is the first question asked afterwards.
/// </summary>
public sealed record RefundOrderCommand(Guid OrderId, Guid? ActorId = null, decimal? Amount = null);

/// <summary>
/// Gives a customer their money back and, once nothing is owed, returns the goods to sale.
///
/// Four rules, in this order, and the order is the design:
///
/// 1. Only a <see cref="OrderStatus.Paid"/> order can be refunded here. Once an order has shipped the
///    money is half the question — the goods are in transit — and that is a returns workflow, not
///    this. Refusing beats pretending.
/// 2. The provider is asked first, and the order is only written once it agrees. Writing the status
///    first would leave an order claiming a refund over money still sitting in the account.
/// 3. A refund that cannot be confirmed is not reported as a plain failure, and changes nothing.
///    Telling staff it failed when it may have succeeded invites a second one, and nothing downstream
///    would notice a double refund.
/// 4. Stock comes back only when the order is fully refunded. A part refund leaves goods owed, so the
///    units stay committed to it.
///
/// Both the provider call and the database write are idempotent. The key is derived from the order and
/// the running total, so repeating one refund returns the original rather than paying out twice, while
/// a genuinely new partial refund is a different key. The write is guarded on the stored total, so two
/// staff refunding at the same moment cannot both apply their amount.
/// </summary>
public sealed class RefundOrderHandler(
    IOrderRepository orders,
    IPaymentGateway payments,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<RefundOrderHandler> logger)
{
    public async Task<Result<OrderView>> Handle(RefundOrderCommand command, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.OrderId, ct);
        if (order is null)
        {
            return Result<OrderView>.Fail("Order not found.");
        }

        if (order.IsProtected)
        {
            // A showcase order. Refunding it is irreversible and nothing re-seeds orders, so the exhibit
            // would simply be gone for the next visitor.
            return Result<OrderView>.Fail(DemoProtection.OrderMessage);
        }

        if (order.Status == OrderStatus.Refunded)
        {
            // Idempotent at this level too: a second click on a fully refunded order is a no-op rather
            // than a second payout.
            return Result<OrderView>.Success(OrderView.From(order));
        }

        if (order.Status != OrderStatus.Paid)
        {
            return Result<OrderView>.Fail($"Only a paid order can be refunded; this one is {order.Status}.");
        }

        var amount = command.Amount ?? order.RefundableRemaining;
        if (amount <= 0)
        {
            return Result<OrderView>.Fail("Refund amount must be positive.");
        }

        if (amount > order.RefundableRemaining)
        {
            // Checked here as well as by the database constraint, so staff get a sentence rather than a
            // 500 — and so the provider is never asked for money the order cannot owe.
            return Result<OrderView>.Fail(
                $"Only {order.RefundableRemaining:0.00} of this order remains refundable.");
        }

        var refundedTotal = order.RefundedTotal + amount;
        var fully = refundedTotal >= order.Total;

        // Derived from the order AND the running total. Retrying this refund presents the same key and
        // returns the original; a second, different partial refund is legitimately a different one. A
        // GUID per click would make every attempt a fresh payout in the provider's eyes.
        var key = $"refund-{order.OrderNumber}-{refundedTotal:0.00}";

        var refund = await payments.RefundAsync(order.PaymentReference ?? string.Empty, amount, key, ct);

        if (refund.Status == PaymentStatus.Indeterminate)
        {
            logger.LogError(
                "Refund of {Amount} on order {OrderNumber} could not be confirmed at {Provider}; the order is unchanged. {Error}",
                amount,
                order.OrderNumber,
                refund.Provider,
                refund.Error);

            // Recorded even though nothing changed: an attempt whose outcome is unknown is exactly what
            // someone needs to find later, and a trail that only holds successes would hide it.
            await audit.WriteAsync(
                command.ActorId,
                "order.refund_unconfirmed",
                $"{order.OrderNumber}: {amount:0.00} via {refund.Provider} — outcome unknown",
                ct);

            return Result<OrderView>.Fail(refund.Error ?? "The refund could not be confirmed.");
        }

        if (refund.Status != PaymentStatus.Succeeded)
        {
            await audit.WriteAsync(
                command.ActorId,
                "order.refund_refused",
                $"{order.OrderNumber}: {amount:0.00} via {refund.Provider} — {refund.Error}",
                ct);

            return Result<OrderView>.Fail(refund.Error ?? "The refund was not accepted.");
        }

        var now = clock.GetUtcNow();
        if (!await orders.RecordRefundAsync(order.Id, refundedTotal, fully, now, ct))
        {
            // The money has left, so this cannot be reported as a failure. Another refund landed first
            // and moved the running total, which is why the guard declined — loudly, because the two
            // payouts now need reconciling by hand.
            logger.LogError(
                "Refund of {Amount} on order {OrderNumber} was accepted by {Provider} but the order had " +
                "already moved; the order needs checking by hand.",
                amount,
                order.OrderNumber,
                refund.Provider);

            await audit.WriteAsync(
                command.ActorId,
                "order.refund_unrecorded",
                $"{order.OrderNumber}: {amount:0.00} paid out via {refund.Provider} but not recorded — needs reconciling",
                ct);

            return Result<OrderView>.Fail("The refund was paid but the order had already changed. Check the order before retrying.");
        }

        order.RefundedTotal = refundedTotal;
        order.Status = fully ? OrderStatus.Refunded : OrderStatus.Paid;

        await audit.WriteAsync(
            command.ActorId,
            fully ? "order.refunded" : "order.refunded_partial",
            $"{order.OrderNumber}: {amount:0.00} via {refund.Provider} (reference {refund.Reference}), {refundedTotal:0.00} of {order.Total:0.00} total",
            ct);

        logger.LogInformation(
            "Refunded {Amount} on order {OrderNumber} at {Provider}; {RefundedTotal} of {Total} refunded.",
            amount,
            order.OrderNumber,
            refund.Provider,
            refundedTotal,
            order.Total);

        return Result<OrderView>.Success(OrderView.From(order));
    }
}
