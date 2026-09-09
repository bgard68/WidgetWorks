using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.IntegrationTests;

/// <summary>
/// The catalogue's sort orders, against real PostgreSQL.
///
/// The sort value arrives from a query string and is mapped to a fixed set of ORDER BY clauses, so
/// an unrecognised one falls back rather than reaching the database. That mapping is only meaningful
/// if the clauses it picks actually order the rows the way the storefront promises, which is a fact
/// about PostgreSQL's collation and not about the C# switch.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CatalogSortTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private WidgetRepository Widgets => new(db.Connections);

    /// <summary>A shared search term so each test sees only the rows it created.</summary>
    private async Task<string> GivenThreeWidgetsAsync()
    {
        var tag = "SORT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Price order and alphabetical order are deliberately different sequences: with prices that
        // ascended alphabetically, a name sort and a price sort would return identical rows and
        // neither test could tell the two clauses apart.
        //
        //   by name:  Alloy (30), Brass (10), Cobalt (20)
        //   by price: Brass (10), Cobalt (20), Alloy (30)
        await AddAsync(tag, name: $"{tag} Cobalt Block", price: 20m);
        await AddAsync(tag, name: $"{tag} Alloy Frame", price: 30m);
        await AddAsync(tag, name: $"{tag} Brass Hub", price: 10m);

        return tag;
    }

    private async Task AddAsync(string tag, string name, decimal price)
        => await Widgets.AddAsync(
            new Widget
            {
                Id = Guid.NewGuid(),
                Sku = tag + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                Name = name,
                Description = "Sort fixture.",
                Price = price,
                QuantityOnHand = 5,
                QuantityReserved = 0,
                IsActive = true,
                CreatedAt = Now,
                UpdatedAt = Now,
            },
            CancellationToken.None);

    private Task<IReadOnlyList<Widget>> SearchAsync(string tag, string? sort)
        => Widgets.SearchAsync(
            new WidgetQuery(tag, ActiveOnly: true, Page: 1, PageSize: 20, Category: null, Sort: sort),
            CancellationToken.None);

    [Fact]
    public async Task Search_SortedByName_ReturnsAlphabeticalOrderRegardlessOfPrice()
    {
        // Arrange — names and prices deliberately disagree, so an alphabetical result cannot be
        // produced by accident from a price clause or from insertion order.
        var tag = await GivenThreeWidgetsAsync();

        // Act
        var sorted = await SearchAsync(tag, WidgetSort.Name);

        // Assert
        Assert.Equal(
            [$"{tag} Alloy Frame", $"{tag} Brass Hub", $"{tag} Cobalt Block"],
            sorted.Select(w => w.Name));
    }

    [Fact]
    public async Task Search_SortedByPriceAscending_ReturnsCheapestFirst()
    {
        // Arrange
        var tag = await GivenThreeWidgetsAsync();

        // Act
        var sorted = await SearchAsync(tag, WidgetSort.PriceAscending);

        // Assert
        Assert.Equal([10m, 20m, 30m], sorted.Select(w => w.Price));
    }

    [Fact]
    public async Task Search_SortIsUnrecognised_FallsBackInsteadOfReachingTheDatabaseWithIt()
    {
        // Arrange — the value a hand-edited query string would carry. A SQL fragment is used on
        // purpose: if it were ever interpolated into the ORDER BY, this query would fail rather
        // than quietly sort.
        var tag = await GivenThreeWidgetsAsync();

        // Act
        var sorted = await SearchAsync(tag, "name; drop table widgets");

        // Assert — answered with the default ordering, and the catalogue is still there.
        Assert.Equal(3, sorted.Count);
        Assert.Equal(3, (await SearchAsync(tag, WidgetSort.Name)).Count);
    }
}
