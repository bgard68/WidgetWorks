using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Pricing;

/// <summary>
/// US sales tax computed from the destination state's base rate.
///
/// SIMPLIFICATION (documented on purpose): real US sales tax is destination-based with thousands of
/// local/county/city jurisdictions, product-category exemptions, and economic-nexus rules. This uses
/// a single state-level base rate as an approximation, behind ITaxCalculator so a real engine
/// (Avalara / TaxJar / Stripe Tax) can drop in with zero checkout changes. States with no state sales
/// tax (AK, DE, MT, NH, OR) correctly yield $0.
/// </summary>
public sealed class StateSalesTaxCalculator : ITaxCalculator
{
    public TaxLine Calculate(string? stateCode, decimal taxableAmount)
    {
        var code = (stateCode ?? string.Empty).Trim().ToUpperInvariant();
        var rate = Rates.TryGetValue(code, out var r) ? r : 0m;
        var amount = Math.Round(taxableAmount * rate, 2, MidpointRounding.AwayFromZero);
        return new TaxLine(code, rate, amount);
    }

    // Base state sales-tax rates (approximate, state-level only) as decimal fractions.
    private static readonly IReadOnlyDictionary<string, decimal> Rates = new Dictionary<string, decimal>
    {
        ["AL"] = 0.0400m, ["AK"] = 0.0000m, ["AZ"] = 0.0560m, ["AR"] = 0.0650m, ["CA"] = 0.0725m,
        ["CO"] = 0.0290m, ["CT"] = 0.0635m, ["DE"] = 0.0000m, ["DC"] = 0.0600m, ["FL"] = 0.0600m,
        ["GA"] = 0.0400m, ["HI"] = 0.0400m, ["ID"] = 0.0600m, ["IL"] = 0.0625m, ["IN"] = 0.0700m,
        ["IA"] = 0.0600m, ["KS"] = 0.0650m, ["KY"] = 0.0600m, ["LA"] = 0.0445m, ["ME"] = 0.0550m,
        ["MD"] = 0.0600m, ["MA"] = 0.0625m, ["MI"] = 0.0600m, ["MN"] = 0.06875m, ["MS"] = 0.0700m,
        ["MO"] = 0.04225m, ["MT"] = 0.0000m, ["NE"] = 0.0550m, ["NV"] = 0.0685m, ["NH"] = 0.0000m,
        ["NJ"] = 0.06625m, ["NM"] = 0.04875m, ["NY"] = 0.0400m, ["NC"] = 0.0475m, ["ND"] = 0.0500m,
        ["OH"] = 0.0575m, ["OK"] = 0.0450m, ["OR"] = 0.0000m, ["PA"] = 0.0600m, ["RI"] = 0.0700m,
        ["SC"] = 0.0600m, ["SD"] = 0.0420m, ["TN"] = 0.0700m, ["TX"] = 0.0625m, ["UT"] = 0.0610m,
        ["VT"] = 0.0600m, ["VA"] = 0.0530m, ["WA"] = 0.0650m, ["WV"] = 0.0600m, ["WI"] = 0.0500m,
        ["WY"] = 0.0400m,
    };
}
