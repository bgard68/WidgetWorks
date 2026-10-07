using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Checkout.Reconcile;

/// <summary>
/// How hard to chase a charge whose outcome the provider never gave. Bound from the
/// <c>Reconciliation</c> configuration section.
/// </summary>
public sealed class ReconciliationOptions
{
    /// <summary>
    /// Most orders probed in one pass. Each probe is a call to the provider, so a backlog is worked
    /// through over several sweeps rather than turning an outage into a rate-limit problem.
    /// </summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>
    /// How long an order may stay unreconciled before it is escalated in the logs.
    ///
    /// Nothing is decided at this point and nothing is released — that is the whole discipline. An
    /// order still unknown after this long means the provider cannot tell us either, and the only
    /// honest response is to put a human in front of it rather than guess. Four hours is long enough
    /// that a provider incident has resolved and a search index has caught up.
    /// </summary>
    public int EscalateAfterHours { get; set; } = 4;

    /// <summary>
    /// How soon to look again while orders remain unresolved. Short, because the customer may already
    /// have been charged and the order is holding stock while nobody knows.
    /// </summary>
    public int BusyIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How long to wait when there is nothing outstanding.
    ///
    /// Long on purpose, and it costs nothing to be: checkout signals the worker directly when it parks
    /// an order, so this is only the backstop for work that predates a restart. Polling a table that is
    /// empty on every healthy day would keep a serverless database awake around the clock to learn
    /// nothing, which is the expense the hourly reservation sweep is already shaped to avoid.
    /// </summary>
    public int IdleIntervalMinutes { get; set; } = 30;

    /// <summary>Turns reconciliation off — for a host that should not run background work.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Finds out what really happened to charges the provider never confirmed, and settles the orders
/// accordingly.
///
/// This is the other half of refusing to treat "we don't know" as "no". <see cref="CheckoutHandler"/>
/// parks such an order in AwaitingPayment with its reservation intact, which is only safe because
/// something comes back to resolve it — otherwise the stock would sit held forever and the customer
/// would never learn whether they had paid. That something is this.
///
/// The ordering discipline throughout: an order is only ever moved on a definite answer. A probe that
/// fails, times out, or finds nothing yet leaves the order exactly as it was for the next pass.
/// Provider search indexes lag their own writes, so an empty result is not evidence of absence, and
/// acting on it would fail orders that had in fact been paid moments earlier.
/// </summary>
public sealed class ReconcileUnconfirmedPaymentsHandler(
    IOrderRepository orders,
    IPaymentGateway payments,
    IEmailSender email,
    IAuditLog audit,
    TimeProvider clock,
    ReconciliationOptions options,
    ILogger<ReconcileUnconfirmedPaymentsHandler> logger)
{
    /// <summary>The outcome of one pass, in the terms worth reporting.</summary>
    public sealed record Summary(int Examined, int Settled, int Failed, int StillUnknown);

    /// <summary>Runs one pass over the unconfirmed orders.</summary>
    public async Task<Summary> Handle(CancellationToken ct)
    {
        var batch = await orders.GetUnconfirmedPaymentsAsync(Math.Max(1, options.BatchSize), ct);
        if (batch.Count == 0)
        {
            return new Summary(0, 0, 0, 0);
        }

        var now = clock.GetUtcNow();
        var escalateBefore = now.AddHours(-Math.Max(1, options.EscalateAfterHours));
        int settled = 0, failed = 0, unknown = 0;

        foreach (var order in batch)
        {
            // Checked between orders rather than only at the top: each settlement is committed on its
            // own, so a shutdown midway through a batch stops cleanly with no half-done work.
            ct.ThrowIfCancellationRequested();

            PaymentResult probe;
            try
            {
                probe = await payments.ProbeAsync(order.OrderNumber, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unanswerable order must not end the pass for the rest of the batch.
                logger.LogWarning(ex, "Probing the charge for order {OrderNumber} threw.", order.OrderNumber);
                unknown++;
                Escalate(order, escalateBefore, "the probe threw");
                continue;
            }

            switch (probe.Status)
            {
                case PaymentStatus.Succeeded:
                    // The money was taken after all. Settling records the reference we never learned
                    // at charge time, after which the provider's webhooks correlate normally again.
                    if (await orders.MarkPaidAsync(order.Id, probe.Provider, probe.Reference ?? string.Empty, now, ct))
                    {
                        settled++;
                        order.Status = OrderStatus.Paid;

                        // The receipt was never sent — checkout could not know there was anything to
                        // confirm. This is the customer's first notice that the order went through.
                        await SendReceiptAsync(order, ct);

                        // A null actor marks a system action. These settlements change what a customer
                        // was charged without anyone pressing a button, so they belong in the same trail
                        // as the ones that do.
                        await audit.WriteAsync(
                            null,
                            "order.reconciled_paid",
                            $"{order.OrderNumber}: charge had succeeded at {probe.Provider} (reference {probe.Reference})",
                            ct);

                        logger.LogInformation(
                            "Reconciled order {OrderNumber}: the charge had succeeded at {Provider}.",
                            order.OrderNumber,
                            probe.Provider);
                    }
                    else
                    {
                        // A webhook got there first, which is the good outcome.
                        logger.LogDebug("Order {OrderNumber} settled before reconciliation reached it.", order.OrderNumber);
                    }

                    break;

                case PaymentStatus.Declined:
                    // A settled refusal, not a failure to answer. Only now is it safe to release the
                    // stock this order has been holding.
                    if (await orders.MarkPaymentFailedAsync(order, probe.Error ?? "Payment failed.", now, ct))
                    {
                        failed++;

                        await audit.WriteAsync(
                            null,
                            "order.reconciled_failed",
                            $"{order.OrderNumber}: charge had not completed at {probe.Provider}; reservation released",
                            ct);

                        logger.LogInformation(
                            "Reconciled order {OrderNumber}: the charge had not completed; reservation released.",
                            order.OrderNumber);
                    }

                    break;

                case PaymentStatus.Pending:
                    // The charge exists and is still working through the provider. Recording the
                    // reference is the valuable part: it hands the order back to the ordinary
                    // webhook path, and clearing the unconfirmed mark returns it to the stale sweep
                    // so it can no longer hold stock indefinitely.
                    if (await orders.RecordPaymentReferenceAsync(order.Id, probe.Provider, probe.Reference ?? string.Empty, now, ct))
                    {
                        logger.LogInformation(
                            "Order {OrderNumber} has a charge in progress at {Provider}; handed back to the webhook path.",
                            order.OrderNumber,
                            probe.Provider);
                    }
                    else
                    {
                        unknown++;
                    }

                    break;

                default:
                    unknown++;
                    Escalate(order, escalateBefore, probe.Error ?? "the provider still cannot say");
                    break;
            }
        }

        if (unknown > 0)
        {
            // One line per pass carrying the count, which is what a log-based alert rule can watch.
            // Per-order errors say which order; this says how bad it is right now.
            logger.LogWarning(
                "{Unknown} order(s) still have an unknown payment outcome after this pass and are holding stock.",
                unknown);
        }

        if (settled > 0 || failed > 0)
        {
            logger.LogInformation(
                "Reconciliation examined {Examined} unconfirmed order(s): {Settled} had been paid, {Failed} had not.",
                batch.Count,
                settled,
                failed);
        }

        return new Summary(batch.Count, settled, failed, unknown);
    }

    /// <summary>
    /// Raises the volume on an order nobody can resolve, once it is old enough that waiting is no
    /// longer the explanation. Deliberately only a log: the order keeps its reservation and its
    /// status, because an automated guess here is how a paid customer gets told they were not.
    /// </summary>
    private void Escalate(Order order, DateTimeOffset escalateBefore, string reason)
    {
        if (order.PaymentUnconfirmedAt is { } since && since < escalateBefore)
        {
            logger.LogError(
                "Order {OrderNumber} has been unreconciled since {Since:o} — {Reason}. It still holds " +
                "its stock reservation and needs a human to check {Provider} and settle it by hand.",
                order.OrderNumber,
                since,
                reason,
                order.PaymentProvider ?? "the provider");
        }
    }

    private async Task SendReceiptAsync(Order order, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(EmailTemplates.OrderReceived(order), ct);
        }
        catch (Exception ex)
        {
            // The order is paid either way; a notification failure must not undo that or stop the
            // rest of the batch. Logged so a missing receipt can be chased.
            logger.LogWarning(ex, "Receipt email failed for reconciled order {OrderNumber}.", order.OrderNumber);
        }
    }
}
