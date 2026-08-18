namespace WidgetWorks.WebApi.Authorization;

/// <summary>Named authorization policies used across endpoints.</summary>
public static class Policies
{
    /// <summary>Manage widgets, inventory, and orders — satisfied by Manager or Administrator.</summary>
    public const string ManageCatalog = "ManageCatalog";

    /// <summary>Manage users and roles — Administrator only.</summary>
    public const string ManageUsers = "ManageUsers";

    /// <summary>
    /// Remove a widget from the catalog — Administrator only. Deliberately
    /// narrower than <see cref="ManageCatalog"/>: a Manager can create, edit,
    /// restock and hide widgets, but not retire one.
    /// </summary>
    public const string DeleteCatalog = "DeleteCatalog";
}
