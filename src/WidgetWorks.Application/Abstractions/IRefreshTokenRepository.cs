using WidgetWorks.Domain.Auth;

namespace WidgetWorks.Application.Abstractions;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken ct);

    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);

    Task UpdateAsync(RefreshToken token, CancellationToken ct);

    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken ct);

    Task RevokeAllForUserAsync(Guid userId, DateTimeOffset revokedAt, CancellationToken ct);
}
