namespace WidgetWorks.Application.Abstractions;

public readonly record struct ShippingQuote(string Method, decimal Amount);

/// <summary>Computes shipping cost for an order. A seam so a carrier-rate service can drop in later.</summary>
public interface IShippingCalculator
{
    ShippingQuote Calculate(string? method, decimal subtotal, int itemCount);

    IReadOnlyList<string> AvailableMethods { get; }
}
