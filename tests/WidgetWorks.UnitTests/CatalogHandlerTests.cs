using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Catalog.Browse;
using WidgetWorks.Application.Catalog.Create;
using WidgetWorks.Application.Catalog.Detail;
using WidgetWorks.Application.Catalog.Inventory;
using WidgetWorks.Application.Catalog.Update;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class CatalogHandlerTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Create_normalizes_sku_and_persists()
    {
        var repo = new InMemoryWidgetRepository();
        var handler = new CreateWidgetHandler(repo, Clock());

        var result = await handler.Handle(
            new CreateWidgetCommand(" ww-100 ", " Gizmo ", "desc", null, 12.50m, 5), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var widget = repo.Store[result.Value];
        Assert.Equal("WW-100", widget.Sku);
        Assert.Equal("Gizmo", widget.Name);
        Assert.Equal(5, widget.QuantityAvailable);
    }

    [Fact]
    public async Task Create_rejects_duplicate_sku()
    {
        var repo = new InMemoryWidgetRepository();
        var handler = new CreateWidgetHandler(repo, Clock());
        await handler.Handle(new CreateWidgetCommand("WW-1", "A", "", null, 1m, 1), CancellationToken.None);

        var dup = await handler.Handle(new CreateWidgetCommand("ww-1", "B", "", null, 1m, 1), CancellationToken.None);

        Assert.True(dup.IsFailure);
    }

    [Fact]
    public async Task Create_rejects_negative_price()
    {
        var repo = new InMemoryWidgetRepository();
        var handler = new CreateWidgetHandler(repo, Clock());

        var result = await handler.Handle(new CreateWidgetCommand("WW-2", "A", "", null, -1m, 1), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Detail_hides_inactive_from_storefront()
    {
        var repo = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-3", Name = "Hidden", IsActive = false, QuantityOnHand = 1 };
        repo.Store[widget.Id] = widget;
        var handler = new GetWidgetHandler(repo);

        var storefront = await handler.Handle(new GetWidgetQuery(widget.Id, IncludeInactive: false), CancellationToken.None);
        var admin = await handler.Handle(new GetWidgetQuery(widget.Id, IncludeInactive: true), CancellationToken.None);

        Assert.True(storefront.IsFailure);
        Assert.True(admin.IsSuccess);
    }

    [Fact]
    public async Task Browse_pages_and_filters_active()
    {
        var repo = new InMemoryWidgetRepository();
        for (var i = 0; i < 5; i++)
        {
            repo.Store[Guid.NewGuid()] = new Widget { Id = Guid.NewGuid(), Sku = $"WW-{i}", Name = $"Widget {i}", IsActive = true, QuantityOnHand = 1 };
        }

        repo.Store[Guid.NewGuid()] = new Widget { Id = Guid.NewGuid(), Sku = "WW-X", Name = "Inactive", IsActive = false, QuantityOnHand = 1 };
        var handler = new BrowseWidgetsHandler(repo);

        var page = await handler.Handle(new BrowseWidgetsQuery(null, IncludeInactive: false, Page: 1, PageSize: 2), CancellationToken.None);

        Assert.Equal(5, page.TotalCount);   // inactive excluded
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalPages);
    }

    [Fact]
    public async Task AdjustInventory_rejects_drop_below_reserved()
    {
        var repo = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-4", Name = "Reserved", IsActive = true, QuantityOnHand = 10, QuantityReserved = 6 };
        repo.Store[widget.Id] = widget;
        var handler = new AdjustInventoryHandler(repo, Clock());

        var tooLow = await handler.Handle(new AdjustInventoryCommand(widget.Id, -5), CancellationToken.None);
        var ok = await handler.Handle(new AdjustInventoryCommand(widget.Id, 5), CancellationToken.None);

        Assert.True(tooLow.IsFailure);
        Assert.True(ok.IsSuccess);
        Assert.Equal(9, ok.Value);   // 15 on hand - 6 reserved
    }

    [Fact]
    public async Task Update_changes_fields_and_active_flag()
    {
        var repo = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-5", Name = "Old", IsActive = true, QuantityOnHand = 1 };
        repo.Store[widget.Id] = widget;
        var handler = new UpdateWidgetHandler(repo, Clock());

        var result = await handler.Handle(new UpdateWidgetCommand(widget.Id, "New", "newdesc", "http://img", 3m, false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("New", repo.Store[widget.Id].Name);
        Assert.False(repo.Store[widget.Id].IsActive);
    }

    [Fact]
    public async Task Create_requires_a_sku()
    {
        var repo = new InMemoryWidgetRepository();
        var handler = new CreateWidgetHandler(repo, Clock());

        var result = await handler.Handle(new CreateWidgetCommand("  ", "Gizmo", "", null, 1m, 1), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("SKU is required.", result.Error);
    }

    [Fact]
    public async Task Update_requires_a_name()
    {
        var repo = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-6", Name = "Named", IsActive = true, QuantityOnHand = 1 };
        repo.Store[widget.Id] = widget;
        var handler = new UpdateWidgetHandler(repo, Clock());

        var result = await handler.Handle(new UpdateWidgetCommand(widget.Id, " ", "", null, 1m, true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Name is required.", result.Error);
        Assert.Equal("Named", repo.Store[widget.Id].Name);   // unchanged
    }

    [Fact]
    public async Task Update_of_an_unknown_widget_fails()
    {
        var handler = new UpdateWidgetHandler(new InMemoryWidgetRepository(), Clock());

        var result = await handler.Handle(new UpdateWidgetCommand(Guid.NewGuid(), "New", "", null, 1m, true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Widget not found.", result.Error);
    }

    [Fact]
    public async Task AdjustInventory_of_an_unknown_widget_fails()
    {
        var handler = new AdjustInventoryHandler(new InMemoryWidgetRepository(), Clock());

        var result = await handler.Handle(new AdjustInventoryCommand(Guid.NewGuid(), 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Widget not found.", result.Error);
    }

    [Fact]
    public async Task AdjustInventory_cannot_take_on_hand_below_zero()
    {
        var repo = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-7", Name = "Scarce", IsActive = true, QuantityOnHand = 3 };
        repo.Store[widget.Id] = widget;
        var handler = new AdjustInventoryHandler(repo, Clock());

        var result = await handler.Handle(new AdjustInventoryCommand(widget.Id, -4), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("On-hand quantity cannot go negative.", result.Error);
        Assert.Equal(3, repo.Store[widget.Id].QuantityOnHand);   // unchanged
    }
}
