using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class RefreshTokenRepository(IDbConnectionFactory factory) : IRefreshTokenRepository
{
    private const string Columns =
        "id, user_id, token_hash, family_id, replaced_by, expires_at, revoked_at, created_at";

    public async Task AddAsync(RefreshToken token, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into refresh_tokens (id, user_id, token_hash, family_id, replaced_by, expires_at, revoked_at, created_at)
              values (@Id, @UserId, @TokenHash, @FamilyId, @ReplacedBy, @ExpiresAt, @RevokedAt, @CreatedAt)",
            token);
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<RefreshToken>(
            $"select {Columns} from refresh_tokens where token_hash = @tokenHash",
            new { tokenHash });
    }

    public async Task UpdateAsync(RefreshToken token, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "update refresh_tokens set replaced_by = @ReplacedBy, revoked_at = @RevokedAt where id = @Id",
            token);
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "update refresh_tokens set revoked_at = @revokedAt where family_id = @familyId and revoked_at is null",
            new { familyId, revokedAt });
    }

    public async Task RevokeAllForUserAsync(Guid userId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "update refresh_tokens set revoked_at = @revokedAt where user_id = @userId and revoked_at is null",
            new { userId, revokedAt });
    }
}
