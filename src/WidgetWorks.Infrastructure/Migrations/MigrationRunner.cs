using System.Reflection;
using DbUp;

namespace WidgetWorks.Infrastructure.Migrations;

public static class MigrationRunner
{
    /// <summary>Creates the database if needed and applies all embedded SQL migrations in order.</summary>
    public static void Run(string connectionString)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException("Database migration failed.", result.Error);
        }
    }
}
