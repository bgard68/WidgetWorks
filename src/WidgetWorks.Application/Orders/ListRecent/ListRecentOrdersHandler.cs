using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Orders.ListMine;

namespace WidgetWorks.Application.Orders.ListRecent;

public sealed record ListRecentOrdersQuery(int Limit);

/// <summary>
/// The staff order list. Without it the admin screen can only look an order up by its GUID, which
/// nobody has to hand — so orders were effectively invisible to Managers and Administrators.
/// </summary>
public sealed class ListRecentOrdersHandler(IOrderRepository orders)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<IReadOnlyList<OrderSummary>> Handle(ListRecentOrdersQuery query, CancellationToken ct)
    {
        var limit = query.Limit is < 1 or > MaxLimit ? DefaultLimit : query.Limit;
        var list = await orders.GetRecentAsync(limit, ct);
        return list.Select(OrderSummary.From).ToList();
    }
}
