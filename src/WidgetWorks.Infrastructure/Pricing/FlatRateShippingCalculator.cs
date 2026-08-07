using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Pricing;

/// <summary>
/// Simple tiered shipping: Standard is free over a threshold, otherwise a base rate plus a
/// per-extra-item surcharge; Express is a higher base plus surcharge. Behind IShippingCalculator so
/// a real carrier-rate integration can replace it without touching checkout.
/// </summary>
public sealed class FlatRateShippingCalculator : IShippingCalculator
{
    private const decimal FreeThreshold = 75m;
    private const string Standard = "Standard";
    private const string Express = "Express";

    public IReadOnlyList<string> AvailableMethods { get; } = [Standard, Express];

    public ShippingQuote Calculate(string? method, decimal subtotal, int itemCount)
    {
        var normalized = string.Equals(method?.Trim(), Express, StringComparison.OrdinalIgnoreCase)
            ? Express
            : Standard;
        var extraItems = Math.Max(0, itemCount - 1);

        var amount = normalized == Express
            ? 19.99m + (extraItems * 1.50m)
            : subtotal >= FreeThreshold ? 0m : 6.99m + (extraItems * 0.75m);

        return new ShippingQuote(normalized, decimal.Round(amount, 2, MidpointRounding.AwayFromZero));
    }
}
