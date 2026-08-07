using WidgetWorks.Domain.Auth;

namespace WidgetWorks.Application.Abstractions;

public interface ITwoFactorRepository
{
    Task UpsertPendingSecretAsync(Guid userId, string secretBase32, CancellationToken ct);

    Task<TwoFactorSecret?> GetSecretAsync(Guid userId, CancellationToken ct);

    Task MarkConfirmedAsync(Guid userId, CancellationToken ct);

    Task DeleteSecretAsync(Guid userId, CancellationToken ct);

    Task AddRecoveryCodesAsync(Guid userId, IReadOnlyList<string> codeHashes, DateTimeOffset now, CancellationToken ct);

    Task DeleteRecoveryCodesAsync(Guid userId, CancellationToken ct);

    Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string codeHash, DateTimeOffset usedAt, CancellationToken ct);
}
