using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Infrastructure.Persistence;

public sealed class UserRepository(IDbConnectionFactory factory) : IUserRepository
{
    private const string Columns =
        "id, email, normalized_email, password_hash, role, security_stamp, is_protected_admin, two_factor_enabled, google_sub, failed_access_count, locked_until, created_at";

    public async Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<User>(
            $"select {Columns} from users where normalized_email = @normalizedEmail",
            new { normalizedEmail });
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<User>(
            $"select {Columns} from users where id = @id",
            new { id });
    }

    public async Task<User?> GetByGoogleSubAsync(string googleSub, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<User>(
            $"select {Columns} from users where google_sub = @googleSub",
            new { googleSub });
    }

    public async Task AddAsync(User user, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"insert into users (id, email, normalized_email, password_hash, role, security_stamp, is_protected_admin, two_factor_enabled, google_sub, failed_access_count, locked_until, created_at)
              values (@Id, @Email, @NormalizedEmail, @PasswordHash, @Role, @SecurityStamp, @IsProtectedAdmin, @TwoFactorEnabled, @GoogleSub, @FailedAccessCount, @LockedUntil, @CreatedAt)",
            user);
    }

    public async Task UpdateAsync(User user, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        await db.ExecuteAsync(
            @"update users set email = @Email, normalized_email = @NormalizedEmail, password_hash = @PasswordHash, role = @Role, security_stamp = @SecurityStamp, two_factor_enabled = @TwoFactorEnabled, google_sub = @GoogleSub, failed_access_count = @FailedAccessCount, locked_until = @LockedUntil where id = @Id",
            user);
    }

    public async Task<Guid?> GetSecurityStampAsync(Guid userId, CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<Guid?>(
            "select security_stamp from users where id = @userId",
            new { userId });
    }
}
