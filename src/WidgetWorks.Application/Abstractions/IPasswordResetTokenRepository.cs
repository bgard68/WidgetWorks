using WidgetWorks.Domain.Auth;

namespace WidgetWorks.Application.Abstractions;

public interface IPasswordResetTokenRepository
{
    Task AddAsync(PasswordResetToken token, CancellationToken ct);

    Task<PasswordResetToken?> GetByHashAsync(string tokenHash, CancellationToken ct);

    Task MarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken ct);

    Task InvalidateForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}
