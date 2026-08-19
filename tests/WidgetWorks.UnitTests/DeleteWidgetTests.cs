using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Catalog.Browse;
using WidgetWorks.Application.Catalog.Delete;
using WidgetWorks.Application.Catalog.Inventory;
using WidgetWorks.Application.Catalog.Update;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Deleting a widget has two outcomes: gone for good when nothing references it,
/// archived when orders do — because order_items has no delete rule and those
/// orders must stay reportable.
/// </summary>
public class DeleteWidgetTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static (InMemoryWidgetRepository Repo, Widget Widget) Seed(int orderLines)
    {
        var repo = new InMemoryWidgetRepository();
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = "WW-900",
            Name = "Doomed",
            IsActive = true,
            QuantityOnHand = 5,
        };
        repo.Store[widget.Id] = widget;
        if (orderLines > 0)
        {
            repo.OrderLines[widget.Id] = orderLines;
        }

        return (repo, widget);
    }

    [Fact]
    public async Task Deletes_outright_when_never_ordered()
    {
        var (repo, widget) = Seed(orderLines: 0);
        var handler = new DeleteWidgetHandler(repo, Clock());

        var result = await handler.Handle(new DeleteWidgetCommand(widget.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DeleteWidgetOutcome.Deleted, result.Value!.Outcome);
        Assert.False(repo.Store.ContainsKey(widget.Id));
    }

    [Fact]
    public async Task Archives_instead_of_deleting_when_it_has_order_history()
    {
        var (repo, widget) = Seed(orderLines: 3);
        var handler = new DeleteWidgetHandler(repo, Clock());

        var result = await handler.Handle(new DeleteWidgetCommand(widget.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DeleteWidgetOutcome.Archived, result.Value!.Outcome);
        Assert.Equal(3, result.Value.OrderLineCount);

        // The row survives so the orders referencing it still resolve.
        var kept = Assert.Contains(widget.Id, repo.Store);
        Assert.True(kept.IsArchived);
        Assert.False(kept.IsActive);
    }

    [Fact]
    public async Task Archived_widget_disappears_from_the_catalog_listing()
    {
        var (repo, widget) = Seed(orderLines: 1);
        await new DeleteWidgetHandler(repo, Clock()).Handle(new DeleteWidgetCommand(widget.Id), CancellationToken.None);

        // Even the admin listing, which includes inactive widgets, must not show it.
        var listing = await new BrowseWidgetsHandler(repo).Handle(
            new BrowseWidgetsQuery(null, IncludeInactive: true, 1, 20), CancellationToken.None);

        Assert.Empty(listing.Items);
    }

    [Fact]
    public async Task Missing_widget_fails()
    {
        var repo = new InMemoryWidgetRepository();
        var handler = new DeleteWidgetHandler(repo, Clock());

        var result = await handler.Handle(new DeleteWidgetCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Archiving_twice_fails()
    {
        var (repo, widget) = Seed(orderLines: 2);
        var handler = new DeleteWidgetHandler(repo, Clock());
        await handler.Handle(new DeleteWidgetCommand(widget.Id), CancellationToken.None);

        var again = await handler.Handle(new DeleteWidgetCommand(widget.Id), CancellationToken.None);

        Assert.True(again.IsFailure);
    }

    [Fact]
    public async Task Archived_widget_cannot_be_edited_or_restocked()
    {
        var (repo, widget) = Seed(orderLines: 1);
        await new DeleteWidgetHandler(repo, Clock()).Handle(new DeleteWidgetCommand(widget.Id), CancellationToken.None);

        var edit = await new UpdateWidgetHandler(repo, Clock()).Handle(
            new UpdateWidgetCommand(widget.Id, "Resurrected", "", null, 5m, IsActive: true), CancellationToken.None);
        var restock = await new AdjustInventoryHandler(repo, Clock()).Handle(
            new AdjustInventoryCommand(widget.Id, 10), CancellationToken.None);

        Assert.True(edit.IsFailure);
        Assert.True(restock.IsFailure);
        Assert.False(repo.Store[widget.Id].IsActive);
    }
}
