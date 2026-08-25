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
        ("WW-001", "Standard Widget Block", "The dependable everyday widget.", 9.99m, 250),
        ("WW-002", "Standard Widget Rotary", "Dial-adjustable everyday widget.", 11.49m, 220),
        ("WW-003", "Standard Widget Valve", "Inline everyday widget with twin ports.", 12.99m, 180),
        ("WW-004", "Standard Widget Hub", "Four-port everyday widget.", 13.99m, 160),
        ("WW-005", "Standard Widget Turbine", "Ventilated everyday widget.", 14.99m, 140),
        ("WW-006", "Deluxe Widget Block", "Premium finish with a reinforced housing.", 24.99m, 120),
        ("WW-007", "Deluxe Widget Rotary", "Premium dial with a gold-plated pointer.", 27.49m, 110),
        ("WW-008", "Deluxe Widget Valve", "Premium inline widget, machined ports.", 29.99m, 90),
        ("WW-009", "Deluxe Widget Hub", "Premium four-port widget, gold contacts.", 32.99m, 80),
        ("WW-010", "Deluxe Widget Turbine", "Premium ventilated widget, balanced rotor.", 34.99m, 70),
        ("WW-011", "Mega Widget Block", "Oversized widget for heavy-duty jobs.", 49.99m, 60),
        ("WW-012", "Mega Widget Rotary", "Oversized dial widget for heavy-duty jobs.", 54.49m, 55),
        ("WW-013", "Mega Widget Valve", "Oversized inline widget, high-flow ports.", 59.99m, 45),
        ("WW-014", "Mega Widget Hub", "Oversized four-port widget for busy lines.", 64.99m, 40),
        ("WW-015", "Mega Widget Turbine", "Oversized ventilated widget, high throughput.", 69.99m, 35),
        ("WW-016", "Mini Widget Block", "Compact widget for tight spaces.", 4.99m, 500),
        ("WW-017", "Mini Widget Rotary", "Compact dial widget for tight spaces.", 5.99m, 460),
        ("WW-018", "Mini Widget Valve", "Compact inline widget, low-flow ports.", 6.49m, 420),
        ("WW-019", "Mini Widget Hub", "Compact four-port widget for tight spaces.", 7.49m, 380),
        ("WW-020", "Mini Widget Turbine", "Compact ventilated widget, quiet running.", 8.49m, 340),
        ("WW-021", "Widget Pro Kit", "Bundle of assorted widgets and accessories.", 79.99m, 40),
        ("WW-022", "Widget Starter Kit", "Open-tray bundle for a first build.", 39.99m, 90),
        ("WW-023", "Widget Builder Kit", "Three-drawer cabinet of sorted widgets.", 99.99m, 30),
        ("WW-024", "Widget Travel Kit", "Soft-sided bundle for work away from the bench.", 49.99m, 60),
        ("WW-025", "Widget Master Kit", "Two-case bundle covering the full range.", 149.99m, 20),
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
