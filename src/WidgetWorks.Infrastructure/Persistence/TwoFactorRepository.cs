using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class TwoFactorRepository(IDbConnectionFactory factory, TimeProvider clock) : ITwoFactorRepository
{
    public async Task UpsertPendingSecretAsync(Guid userId, string secretBase32, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into two_factor_secrets (user_id, secret, is_confirmed, created_at)
              values (@userId, @secret, false, @now)
              on conflict (user_id) do update set secret = excluded.secret, is_confirmed = false, created_at = excluded.created_at",
            new { userId, secret = secretBase32, now = clock.GetUtcNow() });
    }

    public async Task<TwoFactorSecret?> GetSecretAsync(Guid userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<TwoFactorSecret>(
            "select user_id, secret, is_confirmed, created_at from two_factor_secrets where user_id = @userId",
            new { userId });
    }

    public async Task MarkConfirmedAsync(Guid userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            "update two_factor_secrets set is_confirmed = true where user_id = @userId",
            new { userId });
    }

    public async Task DeleteSecretAsync(Guid userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync("delete from two_factor_secrets where user_id = @userId", new { userId });
    }

    public async Task AddRecoveryCodesAsync(Guid userId, IReadOnlyList<string> codeHashes, DateTimeOffset now, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        foreach (var hash in codeHashes)
        {
            await db.ExecuteAsync(
                @"insert into recovery_codes (id, user_id, code_hash, used_at, created_at)
                  values (@id, @userId, @hash, null, @now)",
                new { id = Guid.NewGuid(), userId, hash, now });
        }
    }

    public async Task DeleteRecoveryCodesAsync(Guid userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync("delete from recovery_codes where user_id = @userId", new { userId });
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string codeHash, DateTimeOffset usedAt, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var affected = await db.ExecuteAsync(
            "update recovery_codes set used_at = @usedAt where user_id = @userId and code_hash = @codeHash and used_at is null",
            new { userId, codeHash, usedAt });
        return affected > 0;
    }
}
