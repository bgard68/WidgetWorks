using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Carts;

namespace WidgetWorks.Application.Pricing;

/// <summary>A fully priced cart: the same numbers whether they are being previewed or charged.</summary>
public sealed record PricedCart(
    decimal Subtotal,
    string ShippingMethod,
    decimal Shipping,
    string StateCode,
    decimal TaxRate,
    decimal Tax,
    decimal Total,
    int ItemCount)
{
    public bool IsEmpty => ItemCount == 0;
}

/// <summary>
/// The single place a cart turns into money. Quoting and checkout both go through it, so the total
/// a shopper is shown and the total they are charged are the same calculation rather than two
/// implementations that happen to agree today.
/// </summary>
public sealed class OrderPricer(IShippingCalculator shipping, ITaxCalculator tax)
{
    public PricedCart Price(CartView cart, string? stateCode, string? shippingMethod)
    {
        var quote = shipping.Calculate(shippingMethod, cart.Subtotal, cart.ItemCount);

        // Nothing in the basket, nothing to deliver -- don't quote a delivery charge on it.
        var shippingAmount = cart.ItemCount == 0 ? 0m : quote.Amount;
        var taxLine = tax.Calculate(stateCode, cart.Subtotal);

        return new PricedCart(
            cart.Subtotal,
            quote.Method,
            shippingAmount,
            taxLine.StateCode,
            taxLine.Rate,
            taxLine.Amount,
            cart.Subtotal + shippingAmount + taxLine.Amount,
            cart.ItemCount);
    }
}
