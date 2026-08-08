using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Orders.Admin;

public sealed record GetOrderByIdQuery(Guid OrderId);

/// <summary>Admin/manager view of any order (no ownership check; guarded by policy at the endpoint).</summary>
public sealed class GetOrderByIdHandler(IOrderRepository orders)
{
    public async Task<Result<OrderView>> Handle(GetOrderByIdQuery query, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(query.OrderId, ct);
        return order is null
            ? Result<OrderView>.Fail("Order not found.")
            : Result<OrderView>.Success(OrderView.From(order));
    }
}
