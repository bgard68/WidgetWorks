using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Orders.GetMine;

public sealed record GetMyOrderQuery(Guid UserId, Guid OrderId);

public sealed class GetMyOrderHandler(IOrderRepository orders)
{
    public async Task<Result<OrderView>> Handle(GetMyOrderQuery query, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(query.OrderId, ct);
        if (order is null || order.UserId != query.UserId)
        {
            return Result<OrderView>.Fail("Order not found.");
        }

        return Result<OrderView>.Success(OrderView.From(order));
    }
}
