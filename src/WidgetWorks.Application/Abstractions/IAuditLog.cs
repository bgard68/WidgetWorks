namespace WidgetWorks.Application.Abstractions;

public interface IAuditLog
{
    Task WriteAsync(Guid? userId, string action, string? detail, CancellationToken ct);
}
