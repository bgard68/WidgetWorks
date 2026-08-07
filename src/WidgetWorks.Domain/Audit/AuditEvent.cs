namespace WidgetWorks.Domain.Audit;

public sealed class AuditEvent
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
