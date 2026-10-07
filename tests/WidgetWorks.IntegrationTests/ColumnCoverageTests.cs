using System.Reflection;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// Guards against a column being left out of a repository's SQL.
///
/// This exists because that bug has happened twice. <c>widgets.is_protected</c> was missing from a
/// read list and every row came back <c>false</c>; <c>users.display_name</c> was missing from an
/// insert and every name was silently stored as null. Neither threw. Dapper ignores a property with
/// no matching parameter, and a column absent from a <c>select</c> simply leaves its property at the
/// type default — so both failure modes look exactly like working code.
///
/// The usual answer, a round-trip test per entity asserting each field by hand, is what was tried
/// after the first one. It did not work: the assertions are a fourth hand-maintained copy of the
/// same column list, so a column added later is missed by the test as easily as by the SQL, and the
/// test keeps a name that claims otherwise.
///
/// So these tests enumerate the entity's properties by **reflection** instead. Every writable
/// property is filled with a value chosen to differ from the column's default, the entity is written
/// and read back through the real repository, and every property is compared. Add a property and a
/// column tomorrow and this fails until the SQL carries it — with no list for anyone to update.
///
/// Properties genuinely not written at creation are skipped by name, each with its reason. That is
/// the one list here, it is short, and an unexplained entry in it should not survive review.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ColumnCoverageTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Base = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private UserRepository Users => new(db.Connections);
    private WidgetRepository Widgets => new(db.Connections);
    private OrderRepository Orders => new(db.Connections);

    /// <summary>
    /// Fills every writable property with a value that differs from what the database would supply
    /// on its own. That is the whole trick: a column missing from the insert falls back to its
    /// default, and the comparison then fails because the default is not what we set.
    /// </summary>
    private static T Fill<T>(T entity, int seed)
    {
        foreach (var p in Writable<T>())
        {
            var value = SampleFor(p.PropertyType, p.Name, seed);
            if (value is not null)
            {
                p.SetValue(entity, value);
            }
        }

        return entity;
    }

    private static object? SampleFor(Type type, string name, int seed)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(Guid)) return Guid.NewGuid();
        if (t == typeof(string)) return $"{name}-{seed}";
        if (t == typeof(int)) return seed;
        if (t == typeof(decimal)) return seed + 0.25m;
        if (t == typeof(bool)) return true;
        if (t == typeof(DateTimeOffset)) return Base.AddSeconds(seed);

        // Collections and anything else are not columns on this table; the item tables have their
        // own repositories and their own coverage.
        return null;
    }

    private static IEnumerable<PropertyInfo> Writable<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

    /// <summary>
    /// Compares every property except those named, which are the ones a create does not set.
    /// </summary>
    private static void AssertEveryColumnSurvived<T>(T written, T readBack, params string[] notSetAtCreation)
    {
        var skip = new HashSet<string>(notSetAtCreation, StringComparer.Ordinal);
        var checkedAny = false;

        foreach (var p in Writable<T>())
        {
            if (skip.Contains(p.Name) || SampleFor(p.PropertyType, p.Name, 1) is null)
            {
                continue;
            }

            checkedAny = true;
            Assert.Equal(p.GetValue(written), p.GetValue(readBack));
        }

        // A reflection-driven assertion that silently matched nothing would be the most convincing
        // green in the suite, so the test refuses to pass without having compared something.
        Assert.True(checkedAny, $"No properties of {typeof(T).Name} were compared.");
    }

    [Fact]
    public async Task Every_user_column_survives_a_write_and_read()
    {
        var user = Fill(new User(), 11);
        user.Email = $"cov-{user.Id:N}@example.com";
        user.NormalizedEmail = user.Email.ToUpperInvariant();
        user.Role = UserRoles.Customer;

        await Users.AddAsync(user, CancellationToken.None);
        var stored = await Users.GetByIdAsync(user.Id, CancellationToken.None);

        Assert.NotNull(stored);

        // IsProtectedAdmin is set by the seeder's own statement, never by AddAsync — the protection
        // guard owns that column and a repository insert must not be able to claim it.
        AssertEveryColumnSurvived(user, stored!, nameof(User.IsProtectedAdmin));
    }

    [Fact]
    public async Task Every_widget_column_survives_a_write_and_read()
    {
        var widget = Fill(new Widget(), 22);
        widget.Sku = $"COV-{Guid.NewGuid():N}"[..20];

        await Widgets.AddAsync(widget, CancellationToken.None);
        var stored = await Widgets.GetByIdAsync(widget.Id, CancellationToken.None);

        Assert.NotNull(stored);

        // A widget is not archived at the moment it is created; ArchivedAt is what archiving sets.
        AssertEveryColumnSurvived(widget, stored!, nameof(Widget.ArchivedAt));
    }

    [Fact]
    public async Task Every_order_column_survives_a_write_and_read()
    {
        var order = Fill(new Order(), 33);
        order.UserId = null;
        order.Email = $"cov-{order.Id:N}@example.com";
        order.OrderNumber = $"WW-COV-{Guid.NewGuid():N}"[..20];
        order.Status = OrderStatus.Pending;
        order.ShipCountry = "US";
        order.ShipState = "OH";
        order.TaxState = "OH";
        order.TaxRate = 0.0725m;
        order.Total = 500m;
        order.RefundedTotal = 10m;   // the table checks 0 <= refunded_total <= total
        order.Items = [];

        var placed = await Orders.TryPlaceAsync(order, CancellationToken.None);
        Assert.True(placed);
        var stored = await Orders.GetByIdAsync(order.Id, CancellationToken.None);

        Assert.NotNull(stored);

        // Each of these is written by a later, deliberate step rather than by placing the order:
        // tracking when it ships, the unconfirmed stamp only when a charge outcome is unknown, the
        // refunded total by compare-and-set as refunds are applied, and protection afterwards and
        // only to demo exhibits.
        AssertEveryColumnSurvived(
            order,
            stored!,
            nameof(Order.TrackingNumber),
            nameof(Order.PaymentUnconfirmedAt),
            nameof(Order.RefundedTotal),
            nameof(Order.IsProtected));
    }
}
