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

    /// <summary>
    /// The middle role. Without a seeded Manager the demo cannot show what ManageCatalog actually
    /// buys you — a Manager may create, edit, restock and hide a widget but not retire one, which
    /// is the whole point of the Administrator-only DeleteCatalog policy.
    /// </summary>
    public string DemoManagerEmail { get; set; } = string.Empty;

    public string DemoManagerPassword { get; set; } = string.Empty;
}

public sealed class DbSeeder(IDbConnectionFactory factory, IPasswordHasher hasher, TimeProvider clock)
{
    private static readonly (string Sku, string Name, string Description, decimal Price, int OnHand)[] DemoWidgets =
    [
        ("WW-001", "Standard Widget", "The dependable everyday widget.", 9.99m, 250),
        ("WW-002", "Deluxe Widget", "Premium finish with a reinforced housing.", 24.99m, 120),
        ("WW-003", "Mega Widget", "Oversized widget for heavy-duty jobs.", 49.99m, 60),
        ("WW-004", "Mini Widget", "Compact widget for tight spaces.", 4.99m, 500),
        ("WW-005", "Widget Pro Kit", "Bundle of assorted widgets and accessories.", 79.99m, 40),
    ];

    public async Task SeedAsync(SeedOptions options, CancellationToken ct)
    {
        await UpsertUserAsync(options.DemoAdminEmail, options.DemoAdminPassword, UserRoles.Administrator, isProtected: true, ct);
        await UpsertUserAsync(options.DemoCustomerEmail, options.DemoCustomerPassword, UserRoles.Customer, isProtected: false, ct);
        await UpsertUserAsync(options.DemoManagerEmail, options.DemoManagerPassword, UserRoles.Manager, isProtected: false, ct);
        await SeedWidgetsAsync(ct);
    }

    private async Task UpsertUserAsync(string email, string password, string role, bool isProtected, CancellationToken ct)
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

    private async Task SeedWidgetsAsync(CancellationToken ct)
    {
        using var db = await factory.OpenAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var (sku, name, description, price, onHand) in DemoWidgets)
        {
            var exists = await db.QuerySingleOrDefaultAsync<Guid?>(
                "select id from widgets where sku = @sku",
                new { sku });
            if (exists is not null)
            {
                continue;
            }

            await db.ExecuteAsync(
                @"insert into widgets (id, sku, name, description, image_url, price, is_active, quantity_on_hand, quantity_reserved, created_at, updated_at)
                  values (@Id, @Sku, @Name, @Description, null, @Price, true, @OnHand, 0, @Now, @Now)",
                new
                {
                    Id = Guid.NewGuid(),
                    Sku = sku,
                    Name = name,
                    Description = description,
                    Price = price,
                    OnHand = onHand,
                    Now = now,
                });
        }
    }
}
