namespace WidgetWorks.Application.Abstractions;

/// <summary>The tax applied to an order: the destination state, the rate used, and the amount.</summary>
public readonly record struct TaxLine(string StateCode, decimal Rate, decimal Amount);

/// <summary>Computes US sales tax from the destination state. A seam for a real tax engine later.</summary>
public interface ITaxCalculator
{
    TaxLine Calculate(string? stateCode, decimal taxableAmount);
}
