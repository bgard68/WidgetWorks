using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Orders.ListMine;

public sealed record ListMyOrdersQuery(Guid UserId);

public sealed class ListMyOrdersHandler(IOrderRepository orders)
{
    public async Task<IReadOnlyList<OrderSummary>> Handle(ListMyOrdersQuery query, CancellationToken ct)
    {
        var list = await orders.GetForUserAsync(query.UserId, ct);
        return list.Select(OrderSummary.From).ToList();
    }
}
