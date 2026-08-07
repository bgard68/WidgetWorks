using Dapper;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure.Persistence;

namespace WidgetWorks.Infrastructure.Seeding;

public sealed class SeedOptions
{
    public string DemoAdminEmail { get; set; } = string.Empty;

    public string DemoAdminPassword { get; set; } = string.Empty;

    public string DemoCustomerEmail { get; set; } = string.Empty;

    public string DemoCustomerPassword { get; set; } = string.Empty;
}

public sealed class DbSeeder(IDbConnectionFactory factory, IPasswordHasher hasher, TimeProvider clock)
{
    public async Task SeedAsync(SeedOptions options, CancellationToken ct)
    {
        await UpsertAsync(options.DemoAdminEmail, options.DemoAdminPassword, UserRoles.Administrator, isProtected: true, ct);
        await UpsertAsync(options.DemoCustomerEmail, options.DemoCustomerPassword, UserRoles.Customer, isProtected: false, ct);
    }

    private async Task UpsertAsync(string email, string password, string role, bool isProtected, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        using var db = await factory.OpenAsync(ct);
        var normalized = email.Trim().ToUpperInvariant();
        var exists = await db.QuerySingleOrDefaultAsync<Guid?>(
            "select id from users where normalized_email = @normalized",
            new { normalized });
        if (exists is not null)
        {
            return;
        }

        await db.ExecuteAsync(
            @"insert into users (id, email, normalized_email, password_hash, role, security_stamp, is_protected_admin, two_factor_enabled, google_sub, failed_access_count, locked_until, created_at)
              values (@Id, @Email, @NormalizedEmail, @PasswordHash, @Role, @SecurityStamp, @IsProtectedAdmin, false, null, 0, null, @CreatedAt)",
            new
            {
                Id = Guid.NewGuid(),
                Email = email.Trim(),
                NormalizedEmail = normalized,
                PasswordHash = hasher.Hash(password),
                Role = role,
                SecurityStamp = Guid.NewGuid(),
                IsProtectedAdmin = isProtected,
                CreatedAt = clock.GetUtcNow(),
            });
    }
}
