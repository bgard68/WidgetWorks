namespace WidgetWorks.Application.Abstractions;

/// <summary>A set of tax rates by state code, versioned with the date it took effect and its source.</summary>
public sealed record TaxRateSet(IReadOnlyDictionary<string, decimal> Rates, DateOnly EffectiveOn, string Source);

/// <summary>
/// Supplies the current tax rates. A seam so the SOURCE of rates can change (offline table today; a
/// tax-as-a-service provider or a scheduled importer tomorrow) without touching the calculator.
/// </summary>
public interface ITaxRateProvider
{
    TaxRateSet Current { get; }
}
