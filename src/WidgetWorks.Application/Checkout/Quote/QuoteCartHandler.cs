using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Carts;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Checkout.Quote;

public sealed record QuoteCartCommand(Guid CartId, string? StateCode, string? ShippingMethod);

/// <summary>Order total breakdown. TaxRate is a fraction (e.g. 0.0725 = 7.25%).</summary>
public sealed record OrderQuoteView(
    decimal Subtotal,
    string ShippingMethod,
    decimal Shipping,
    string StateCode,
    decimal TaxRate,
    decimal Tax,
    decimal Total,
    int ItemCount,
    bool IsEmpty);

public sealed class QuoteCartHandler(
    ICartRepository carts,
    IWidgetRepository widgets,
    OrderPricer pricer)
{
    public async Task<Result<OrderQuoteView>> Handle(QuoteCartCommand command, CancellationToken ct)
    {
        var cart = await carts.GetAsync(command.CartId, ct);
        if (cart is null)
        {
            return Result<OrderQuoteView>.Fail("Cart not found.");
        }

        var view = await CartAssembler.BuildAsync(cart, widgets, ct);
        var priced = pricer.Price(view, command.StateCode, command.ShippingMethod);

        return Result<OrderQuoteView>.Success(new OrderQuoteView(
            priced.Subtotal,
            priced.ShippingMethod,
            priced.Shipping,
            priced.StateCode,
            priced.TaxRate,
            priced.Tax,
            priced.Total,
            priced.ItemCount,
            priced.IsEmpty));
    }
}
