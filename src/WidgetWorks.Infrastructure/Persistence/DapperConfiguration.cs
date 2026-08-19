using Dapper;

namespace WidgetWorks.Infrastructure.Persistence;

/// <summary>
/// Dapper's column-to-property mapping is global, process-wide state. It used to be set inline in
/// AddInfrastructure, which meant any repository constructed outside the DI container -- a test, a
/// console tool, a migration script -- silently mis-mapped every multi-word column: order_number,
/// tracking_number and quantity_reserved came back as defaults while single-word columns worked, so
/// the failure looked like missing data rather than missing configuration.
///
/// Applying it here, once and idempotently, makes the requirement explicit and callable.
/// </summary>
public static class DapperConfiguration
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _applied = true;
    }
}
