using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Catalog.Create;

public sealed record CreateWidgetCommand(
    string Sku,
    string Name,
    string Description,
    string? ImageUrl,
    decimal Price,
    int QuantityOnHand);

public sealed class CreateWidgetHandler(IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result<Guid>> Handle(CreateWidgetCommand command, CancellationToken ct)
    {
        var error = CatalogValidation.ValidateNew(command.Sku, command.Name, command.Price, command.QuantityOnHand);
        if (error is not null)
        {
            return Result<Guid>.Fail(error);
        }

        var normalizedSku = command.Sku.Trim().ToUpperInvariant();
        if (await widgets.GetBySkuAsync(normalizedSku, ct) is not null)
        {
            return Result<Guid>.Fail("A widget with this SKU already exists.");
        }

        var now = clock.GetUtcNow();
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            Sku = normalizedSku,
            Name = command.Name.Trim(),
            Description = command.Description?.Trim() ?? string.Empty,
            ImageUrl = string.IsNullOrWhiteSpace(command.ImageUrl) ? null : command.ImageUrl.Trim(),
            Price = command.Price,
            IsActive = true,
            QuantityOnHand = command.QuantityOnHand,
            QuantityReserved = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await widgets.AddAsync(widget, ct);
        return Result<Guid>.Success(widget.Id);
    }
}
