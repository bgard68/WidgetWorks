using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Pricing;

/// <summary>
/// US sales tax computed from the destination state's base rate, read from an <see cref="ITaxRateProvider"/>.
///
/// SIMPLIFICATION (documented on purpose): real US sales tax is destination-based with thousands of
/// local/county/city jurisdictions, product-category exemptions, and economic-nexus rules. This uses
/// a single state-level base rate as an approximation, behind ITaxCalculator so a real engine
/// (Avalara / TaxJar / Stripe Tax) can drop in with zero checkout changes. States with no state sales
/// tax (AK, DE, MT, NH, OR) correctly yield $0. Rate currency is the rate provider's responsibility --
/// see ADR-022.
/// </summary>
public sealed class StateSalesTaxCalculator(ITaxRateProvider rates) : ITaxCalculator
{
    public TaxLine Calculate(string? stateCode, decimal taxableAmount)
    {
        var code = (stateCode ?? string.Empty).Trim().ToUpperInvariant();
        var rate = rates.Current.Rates.TryGetValue(code, out var r) ? r : 0m;
        var amount = Math.Round(taxableAmount * rate, 2, MidpointRounding.AwayFromZero);
        return new TaxLine(code, rate, amount);
    }
}
