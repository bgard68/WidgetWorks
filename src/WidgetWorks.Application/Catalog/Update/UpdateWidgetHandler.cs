using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Catalog.Update;

public sealed record UpdateWidgetCommand(
    Guid Id,
    string Name,
    string Description,
    string? ImageUrl,
    decimal Price,
    bool IsActive);

public sealed class UpdateWidgetHandler(IWidgetRepository widgets, TimeProvider clock)
{
    public async Task<Result> Handle(UpdateWidgetCommand command, CancellationToken ct)
    {
        var error = CatalogValidation.ValidateEdit(command.Name, command.Price);
        if (error is not null)
        {
            return Result.Fail(error);
        }

        var widget = await widgets.GetByIdAsync(command.Id, ct);
        if (widget is null)
        {
            return Result.Fail("Widget not found.");
        }

        if (widget.IsArchived)
        {
            return Result.Fail("Widget is archived and can no longer be edited.");
        }

        widget.Name = command.Name.Trim();
        widget.Description = command.Description?.Trim() ?? string.Empty;
        widget.ImageUrl = string.IsNullOrWhiteSpace(command.ImageUrl) ? null : command.ImageUrl.Trim();
        widget.Price = command.Price;
        widget.IsActive = command.IsActive;
        widget.UpdatedAt = clock.GetUtcNow();

        await widgets.UpdateAsync(widget, ct);
        return Result.Success();
    }
}
