using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Carts;
using WidgetWorks.Domain.Catalog;

namespace WidgetWorks.Application.Carts;

/// <summary>Builds a priced <see cref="CartView"/> from a cart and the current widget data.</summary>
internal static class CartAssembler
{
    public static async Task<CartView> BuildAsync(Cart cart, IWidgetRepository widgets, CancellationToken ct)
    {
        var map = new Dictionary<Guid, Widget>();
        foreach (var id in cart.Items.Select(i => i.WidgetId).Distinct())
        {
            if (await widgets.GetByIdAsync(id, ct) is { } widget)
            {
                map[id] = widget;
            }
        }

        return Build(cart, map);
    }

    public static CartView Build(Cart cart, IReadOnlyDictionary<Guid, Widget> widgets)
    {
        var lines = new List<CartLineView>();
        foreach (var item in cart.Items)
        {
            if (!widgets.TryGetValue(item.WidgetId, out var w))
            {
                continue;
            }

            lines.Add(new CartLineView(w.Id, w.Sku, w.Name, w.Price, item.Quantity, w.QuantityAvailable, w.Price * item.Quantity));
        }

        var subtotal = lines.Sum(l => l.LineSubtotal);
        var count = lines.Sum(l => l.Quantity);
        return new CartView(cart.Id, cart.UserId, lines, subtotal, count);
    }
}
