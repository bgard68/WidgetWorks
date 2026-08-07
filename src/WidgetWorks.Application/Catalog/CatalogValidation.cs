namespace WidgetWorks.Application.Catalog;

/// <summary>Shared field validation for catalog write operations.</summary>
internal static class CatalogValidation
{
    public static string? ValidateNew(string? sku, string? name, decimal price, int quantityOnHand)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return "SKU is required.";
        }

        var nameError = ValidateEdit(name, price);
        if (nameError is not null)
        {
            return nameError;
        }

        return quantityOnHand < 0 ? "Quantity on hand cannot be negative." : null;
    }

    public static string? ValidateEdit(string? name, decimal price)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Name is required.";
        }

        return price < 0 ? "Price cannot be negative." : null;
    }
}
