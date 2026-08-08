using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class PasswordResetTokenRepository(IDbConnectionFactory factory) : IPasswordResetTokenRepository
{
    public async Task AddAsync(PasswordResetToken token, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into password_reset_tokens (id, user_id, token_hash, expires_at, used_at, created_at)
              values (@Id, @UserId, @TokenHash, @ExpiresAt, @UsedAt, @CreatedAt)",
            token);
    }

    public async Task<PasswordResetToken?> GetByHashAsync(string tokenHash, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<PasswordResetToken>(
            "select id, user_id, token_hash, expires_at, used_at, created_at from password_reset_tokens where token_hash = @tokenHash",
            new { tokenHash });
    }

    public async Task MarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "update password_reset_tokens set used_at = @now where id = @id",
            new { id, now });
    }

    public async Task InvalidateForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "update password_reset_tokens set used_at = @now where user_id = @userId and used_at is null",
            new { userId, now });
    }
}
