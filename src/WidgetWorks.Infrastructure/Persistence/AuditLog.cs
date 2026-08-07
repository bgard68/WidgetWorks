using Dapper;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class AuditLog(IDbConnectionFactory factory, TimeProvider clock) : IAuditLog
{
    public async Task WriteAsync(Guid? userId, string action, string? detail, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into audit_events (id, user_id, action, detail, created_at)
              values (@Id, @UserId, @Action, @Detail, @CreatedAt)",
            new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Action = action,
                Detail = detail,
                CreatedAt = clock.GetUtcNow(),
            });
    }
}
