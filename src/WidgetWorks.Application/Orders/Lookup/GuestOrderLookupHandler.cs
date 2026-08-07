using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Orders.Lookup;

public sealed record GuestOrderLookupQuery(string OrderNumber, string Email);

public sealed class GuestOrderLookupHandler(IOrderRepository orders)
{
    public async Task<Result<OrderView>> Handle(GuestOrderLookupQuery query, CancellationToken ct)
    {
        var number = (query.OrderNumber ?? string.Empty).Trim();
        var email = (query.Email ?? string.Empty).Trim();
        if (number.Length == 0 || email.Length == 0)
        {
            return Result<OrderView>.Fail("Order number and email are required.");
        }

        var order = await orders.GetByNumberAndEmailAsync(number, email, ct);
        return order is null
            ? Result<OrderView>.Fail("Order not found.")
            : Result<OrderView>.Success(OrderView.From(order));
    }
}
