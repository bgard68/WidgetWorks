using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Orders.Admin;

/// <summary>
/// How far apart two orders may be and still look like the same purchase made twice. Bound from the
/// <c>OrderReview</c> configuration section.
/// </summary>
public sealed class OrderReviewOptions
{
    /// <summary>
    /// The window either side of an order in which a matching one counts as a possible duplicate.
    ///
    /// Ten minutes is the span of a mistake: a shopper who double-submitted, reloaded, or tried again
    /// because nothing seemed to happen. Stretch it to hours and you start flagging customers who
    /// genuinely ordered the same thing twice in an afternoon, and a review queue that cries wolf is a
    /// review queue nobody opens.
    /// </summary>
    public int DuplicateWindowMinutes { get; set; } = 10;

    /// <summary>Most rows either list returns. A review queue is for reading, not for paging through.</summary>
    public int Limit { get; set; } = 100;
}

/// <summary>Orders whose charge outcome is still unknown, and orders that look like duplicates.</summary>
public sealed record PaymentExceptionsView(
    IReadOnlyList<OrderSummary> Unconfirmed,
    IReadOnlyList<OrderSummary> PossibleDuplicates);

/// <summary>
/// The staff view of everything on the payment path that needs a person to look at it.
///
/// Both halves existed only as log lines before, which is the same as not existing: an order holding
/// stock with an unknown payment outcome, or a customer charged twice, is not something anyone should
/// have to grep for. Reconciliation resolves nearly all of the first kind on its own and this list is
/// empty on a healthy day — which is the point. It is where you look when it is not.
/// </summary>
public sealed class ListPaymentExceptionsHandler(IOrderRepository orders, OrderReviewOptions options)
{
    public async Task<PaymentExceptionsView> Handle(CancellationToken ct)
    {
        var limit = Math.Max(1, options.Limit);

        var unconfirmed = await orders.GetUnconfirmedPaymentsAsync(limit, ct);
        var duplicates = await orders.GetPossibleDuplicatesAsync(
            TimeSpan.FromMinutes(Math.Max(1, options.DuplicateWindowMinutes)), limit, ct);

        return new PaymentExceptionsView(
            unconfirmed.Select(OrderSummary.From).ToList(),
            duplicates.Select(OrderSummary.From).ToList());
    }
}
