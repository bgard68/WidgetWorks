using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Carts;
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
    IShippingCalculator shipping,
    ITaxCalculator tax)
{
    public async Task<Result<OrderQuoteView>> Handle(QuoteCartCommand command, CancellationToken ct)
    {
        var cart = await carts.GetAsync(command.CartId, ct);
        if (cart is null)
        {
            return Result<OrderQuoteView>.Fail("Cart not found.");
        }

        var view = await CartAssembler.BuildAsync(cart, widgets, ct);
        var ship = shipping.Calculate(command.ShippingMethod, view.Subtotal, view.ItemCount);
        var shippingAmount = view.ItemCount == 0 ? 0m : ship.Amount;
        var taxLine = tax.Calculate(command.StateCode, view.Subtotal);
        var total = view.Subtotal + shippingAmount + taxLine.Amount;

        var quote = new OrderQuoteView(
            view.Subtotal,
            ship.Method,
            shippingAmount,
            taxLine.StateCode,
            taxLine.Rate,
            taxLine.Amount,
            total,
            view.ItemCount,
            view.ItemCount == 0);
        return Result<OrderQuoteView>.Success(quote);
    }
}
