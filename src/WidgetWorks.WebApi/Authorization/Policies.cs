namespace WidgetWorks.WebApi.Authorization;

/// <summary>Named authorization policies used across endpoints.</summary>
public static class Policies
{
    /// <summary>Manage widgets, inventory, and orders — satisfied by Manager or Administrator.</summary>
    public const string ManageCatalog = "ManageCatalog";

    /// <summary>Manage users and roles — Administrator only.</summary>
    public const string ManageUsers = "ManageUsers";
}
