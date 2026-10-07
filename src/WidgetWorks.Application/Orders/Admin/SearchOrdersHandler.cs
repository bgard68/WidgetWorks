using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Orders.Admin;

public sealed record SearchOrdersQuery(string Term, int Limit);

/// <summary>
/// Finds an order for a member of staff, by order number or by the customer's email.
///
/// This existed nowhere. Staff could see the fifty most recent orders or fetch one by its GUID —
/// and nobody has a GUID. A customer ringing up about an order from three months ago was therefore
/// unfindable, which quietly undermined every staff action built on top of it: you cannot refund,
/// cancel or re-ship an order you cannot reach.
///
/// Two inputs because customers arrive with whichever they have. The order number is on their
/// confirmation email; the email address is what they remember. Searching on both is the difference
/// between a search box and a party trick.
/// </summary>
public sealed class SearchOrdersHandler(IOrderRepository orders)
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    /// <summary>
    /// Shorter than this and the search stops being a search. Two characters against an email column
    /// would return a sizeable slice of the customer list, which is not something a search box should
    /// hand over by accident.
    /// </summary>
    public const int MinimumTermLength = 3;

    public async Task<IReadOnlyList<OrderSummary>> Handle(SearchOrdersQuery query, CancellationToken ct)
    {
        var term = (query.Term ?? string.Empty).Trim();
        if (term.Length < MinimumTermLength)
        {
            return [];
        }

        var limit = query.Limit is < 1 or > MaxLimit ? DefaultLimit : query.Limit;
        var found = await orders.SearchAsync(term, limit, ct);
        return found.Select(OrderSummary.From).ToList();
    }
}
