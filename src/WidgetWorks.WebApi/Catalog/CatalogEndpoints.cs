using WidgetWorks.Application.Catalog.Browse;
using WidgetWorks.Application.Catalog.Create;
using WidgetWorks.Application.Catalog.Delete;

using WidgetWorks.Application.Catalog.Detail;
using WidgetWorks.Application.Catalog.Inventory;
using WidgetWorks.Application.Catalog.Update;
using WidgetWorks.WebApi.Authorization;

namespace WidgetWorks.WebApi.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        // Public storefront: active widgets only.
        var catalog = routes.MapGroup("/catalog");

        catalog.MapGet("/widgets", async (string? search, string? category, string? sort, int? page, int? pageSize, BrowseWidgetsHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new BrowseWidgetsQuery(search, IncludeInactive: false, page ?? 1, pageSize ?? 20, category, sort), ct);
            return Results.Ok(result);
        });

        catalog.MapGet("/widgets/{id:guid}", async (Guid id, GetWidgetHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetWidgetQuery(id, IncludeInactive: false), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        // Admin catalog management: Manager or Administrator (ManageCatalog policy).
        var admin = routes.MapGroup("/admin/catalog").RequireAuthorization(Policies.ManageCatalog);

        admin.MapGet("/widgets", async (string? search, int? page, int? pageSize, BrowseWidgetsHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new BrowseWidgetsQuery(search, IncludeInactive: true, page ?? 1, pageSize ?? 20), ct);
            return Results.Ok(result);
        });

        admin.MapGet("/widgets/{id:guid}", async (Guid id, GetWidgetHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetWidgetQuery(id, IncludeInactive: true), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
        });

        admin.MapPost("/widgets", async (CreateWidgetCommand command, CreateWidgetHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(command, ct);
            return result.IsSuccess
                ? Results.Created($"/catalog/widgets/{result.Value}", new { id = result.Value })
                : Results.BadRequest(new { error = result.Error });
        });

        admin.MapPut("/widgets/{id:guid}", async (Guid id, UpdateWidgetRequest body, UpdateWidgetHandler handler, CancellationToken ct) =>
        {
            var command = new UpdateWidgetCommand(id, body.Name, body.Description, body.ImageUrl, body.Price, body.IsActive);
            var result = await handler.Handle(command, ct);
            return result.IsSuccess ? Results.NoContent() : Results.BadRequest(new { error = result.Error });
        });

        // Administrator only (DeleteCatalog), unlike the rest of this group: a widget
        // that has been ordered is archived rather than deleted so those orders stay
        // reportable. The response says which happened.
        admin.MapDelete("/widgets/{id:guid}", async (Guid id, DeleteWidgetHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new DeleteWidgetCommand(id), ct);
            return result is { IsSuccess: true, Value: { } outcome }
                ? Results.Ok(new
                {
                    outcome = outcome.Outcome.ToString(),
                    orderLineCount = outcome.OrderLineCount,
                })
                : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization(Policies.DeleteCatalog);

        admin.MapPost("/widgets/{id:guid}/inventory", async (Guid id, AdjustInventoryRequest body, AdjustInventoryHandler handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new AdjustInventoryCommand(id, body.QuantityOnHandDelta), ct);
            return result.IsSuccess
                ? Results.Ok(new { quantityAvailable = result.Value })
                : Results.BadRequest(new { error = result.Error });
        });
    }

    public sealed record UpdateWidgetRequest(string Name, string Description, string? ImageUrl, decimal Price, bool IsActive);

    public sealed record AdjustInventoryRequest(int QuantityOnHandDelta);
}
