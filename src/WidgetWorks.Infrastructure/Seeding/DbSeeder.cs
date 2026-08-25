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
        ("WW-001", "Standard Widget Block Cobalt", "The dependable everyday widget.", 9.99m, 220),
        ("WW-002", "Standard Widget Rotary Cobalt", "Dial-adjustable everyday widget.", 11.49m, 220),
        ("WW-003", "Standard Widget Valve Cobalt", "Inline everyday widget with twin ports.", 12.99m, 220),
        ("WW-004", "Standard Widget Hub Cobalt", "Four-port everyday widget.", 13.99m, 220),
        ("WW-005", "Standard Widget Turbine Cobalt", "Ventilated everyday widget.", 14.99m, 220),
        ("WW-006", "Deluxe Widget Block Fuchsia", "Premium finish with a reinforced housing.", 24.99m, 110),
        ("WW-007", "Deluxe Widget Rotary Fuchsia", "Premium dial with a gold-plated pointer.", 27.49m, 110),
        ("WW-008", "Deluxe Widget Valve Fuchsia", "Premium inline widget, machined ports.", 29.99m, 110),
        ("WW-009", "Deluxe Widget Hub Fuchsia", "Premium four-port widget, gold contacts.", 32.99m, 110),
        ("WW-010", "Deluxe Widget Turbine Fuchsia", "Premium ventilated widget, balanced rotor.", 34.99m, 110),
        ("WW-011", "Mega Widget Block Copper", "Oversized widget for heavy-duty jobs.", 49.99m, 50),
        ("WW-012", "Mega Widget Rotary Copper", "Oversized dial widget for heavy-duty jobs.", 54.49m, 50),
        ("WW-013", "Mega Widget Valve Copper", "Oversized inline widget, high-flow ports.", 59.99m, 50),
        ("WW-014", "Mega Widget Hub Copper", "Oversized four-port widget for busy lines.", 64.99m, 50),
        ("WW-015", "Mega Widget Turbine Copper", "Oversized ventilated widget, high throughput.", 69.99m, 50),
        ("WW-016", "Mini Widget Block Jade", "Compact widget for tight spaces.", 4.99m, 420),
        ("WW-017", "Mini Widget Rotary Jade", "Compact dial widget for tight spaces.", 5.99m, 420),
        ("WW-018", "Mini Widget Valve Jade", "Compact inline widget, low-flow ports.", 6.49m, 420),
        ("WW-019", "Mini Widget Hub Jade", "Compact four-port widget for tight spaces.", 7.49m, 420),
        ("WW-020", "Mini Widget Turbine Jade", "Compact ventilated widget, quiet running.", 8.49m, 420),
        ("WW-021", "Widget Pro Kit Plum", "Bundle of assorted widgets and accessories.", 79.99m, 45),
        ("WW-022", "Widget Starter Kit Plum", "Open-tray bundle for a first build.", 39.99m, 45),
        ("WW-023", "Widget Builder Kit Plum", "Three-drawer cabinet of sorted widgets.", 99.99m, 45),
        ("WW-024", "Widget Travel Kit Plum", "Soft-sided bundle for work away from the bench.", 49.99m, 45),
        ("WW-025", "Widget Master Kit Plum", "Two-case bundle covering the full range.", 149.99m, 45),
        ("WW-026", "Standard Widget Block Azure", "The dependable everyday widget.", 8.99m, 220),
        ("WW-027", "Standard Widget Rotary Azure", "Dial-adjustable everyday widget.", 10.34m, 220),
        ("WW-028", "Standard Widget Valve Azure", "Inline everyday widget with twin ports.", 11.69m, 220),
        ("WW-029", "Standard Widget Hub Azure", "Four-port everyday widget.", 12.59m, 220),
        ("WW-030", "Standard Widget Turbine Azure", "Ventilated everyday widget.", 13.49m, 220),
        ("WW-031", "Deluxe Widget Block Rose", "Premium finish with a reinforced housing.", 22.49m, 110),
        ("WW-032", "Deluxe Widget Rotary Rose", "Premium dial with a gold-plated pointer.", 24.74m, 110),
        ("WW-033", "Deluxe Widget Valve Rose", "Premium inline widget, machined ports.", 26.99m, 110),
        ("WW-034", "Deluxe Widget Hub Rose", "Premium four-port widget, gold contacts.", 29.69m, 110),
        ("WW-035", "Deluxe Widget Turbine Rose", "Premium ventilated widget, balanced rotor.", 31.49m, 110),
        ("WW-036", "Mega Widget Block Amber", "Oversized widget for heavy-duty jobs.", 44.99m, 50),
        ("WW-037", "Mega Widget Rotary Amber", "Oversized dial widget for heavy-duty jobs.", 49.04m, 50),
        ("WW-038", "Mega Widget Valve Amber", "Oversized inline widget, high-flow ports.", 53.99m, 50),
        ("WW-039", "Mega Widget Hub Amber", "Oversized four-port widget for busy lines.", 58.49m, 50),
        ("WW-040", "Mega Widget Turbine Amber", "Oversized ventilated widget, high throughput.", 62.99m, 50),
        ("WW-041", "Mini Widget Block Teal", "Compact widget for tight spaces.", 4.49m, 420),
        ("WW-042", "Mini Widget Rotary Teal", "Compact dial widget for tight spaces.", 5.39m, 420),
        ("WW-043", "Mini Widget Valve Teal", "Compact inline widget, low-flow ports.", 5.84m, 420),
        ("WW-044", "Mini Widget Hub Teal", "Compact four-port widget for tight spaces.", 6.74m, 420),
        ("WW-045", "Mini Widget Turbine Teal", "Compact ventilated widget, quiet running.", 7.64m, 420),
        ("WW-046", "Widget Pro Kit Violet", "Bundle of assorted widgets and accessories.", 71.99m, 45),
        ("WW-047", "Widget Starter Kit Violet", "Open-tray bundle for a first build.", 35.99m, 45),
        ("WW-048", "Widget Builder Kit Violet", "Three-drawer cabinet of sorted widgets.", 89.99m, 45),
        ("WW-049", "Widget Travel Kit Violet", "Soft-sided bundle for work away from the bench.", 44.99m, 45),
        ("WW-050", "Widget Master Kit Violet", "Two-case bundle covering the full range.", 134.99m, 45),
        ("WW-051", "Standard Widget Block Indigo", "The dependable everyday widget.", 11.49m, 220),
        ("WW-052", "Standard Widget Rotary Indigo", "Dial-adjustable everyday widget.", 13.21m, 220),
        ("WW-053", "Standard Widget Valve Indigo", "Inline everyday widget with twin ports.", 14.94m, 220),
        ("WW-054", "Standard Widget Hub Indigo", "Four-port everyday widget.", 16.09m, 220),
        ("WW-055", "Standard Widget Turbine Indigo", "Ventilated everyday widget.", 17.24m, 220),
        ("WW-056", "Deluxe Widget Block Crimson", "Premium finish with a reinforced housing.", 28.74m, 110),
        ("WW-057", "Deluxe Widget Rotary Crimson", "Premium dial with a gold-plated pointer.", 31.61m, 110),
        ("WW-058", "Deluxe Widget Valve Crimson", "Premium inline widget, machined ports.", 34.49m, 110),
        ("WW-059", "Deluxe Widget Hub Crimson", "Premium four-port widget, gold contacts.", 37.94m, 110),
        ("WW-060", "Deluxe Widget Turbine Crimson", "Premium ventilated widget, balanced rotor.", 40.24m, 110),
        ("WW-061", "Mega Widget Block Bronze", "Oversized widget for heavy-duty jobs.", 57.49m, 50),
        ("WW-062", "Mega Widget Rotary Bronze", "Oversized dial widget for heavy-duty jobs.", 62.66m, 50),
        ("WW-063", "Mega Widget Valve Bronze", "Oversized inline widget, high-flow ports.", 68.99m, 50),
        ("WW-064", "Mega Widget Hub Bronze", "Oversized four-port widget for busy lines.", 74.74m, 50),
        ("WW-065", "Mega Widget Turbine Bronze", "Oversized ventilated widget, high throughput.", 80.49m, 50),
        ("WW-066", "Mini Widget Block Mint", "Compact widget for tight spaces.", 5.74m, 420),
        ("WW-067", "Mini Widget Rotary Mint", "Compact dial widget for tight spaces.", 6.89m, 420),
        ("WW-068", "Mini Widget Valve Mint", "Compact inline widget, low-flow ports.", 7.46m, 420),
        ("WW-069", "Mini Widget Hub Mint", "Compact four-port widget for tight spaces.", 8.61m, 420),
        ("WW-070", "Mini Widget Turbine Mint", "Compact ventilated widget, quiet running.", 9.76m, 420),
        ("WW-071", "Widget Pro Kit Slate", "Bundle of assorted widgets and accessories.", 91.99m, 45),
        ("WW-072", "Widget Starter Kit Slate", "Open-tray bundle for a first build.", 45.99m, 45),
        ("WW-073", "Widget Builder Kit Slate", "Three-drawer cabinet of sorted widgets.", 114.99m, 45),
        ("WW-074", "Widget Travel Kit Slate", "Soft-sided bundle for work away from the bench.", 57.49m, 45),
        ("WW-075", "Widget Master Kit Slate", "Two-case bundle covering the full range.", 172.49m, 45),
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
