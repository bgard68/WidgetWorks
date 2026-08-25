using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Carts;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.UnitTests.Fakes;

public sealed class InMemoryUserRepository : IUserRepository
{
    public readonly Dictionary<Guid, User> Store = new();

    public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct)
        => Task.FromResult(Store.Values.FirstOrDefault(u => u.NormalizedEmail == normalizedEmail));

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct)
        => Task.FromResult(Store.TryGetValue(id, out var u) ? u : null);

    public Task<User?> GetByGoogleSubAsync(string googleSub, CancellationToken ct)
        => Task.FromResult(Store.Values.FirstOrDefault(u => u.GoogleSub == googleSub));

    public Task AddAsync(User user, CancellationToken ct)
    {
        Store[user.Id] = user;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user, CancellationToken ct)
    {
        Store[user.Id] = user;
        return Task.CompletedTask;
    }

    public Task<Guid?> GetSecurityStampAsync(Guid userId, CancellationToken ct)
        => Task.FromResult(Store.TryGetValue(userId, out var u) ? (Guid?)u.SecurityStamp : null);
}

public sealed class InMemoryWidgetRepository : IWidgetRepository
{
    public readonly Dictionary<Guid, Widget> Store = new();

    /// <summary>Order lines per widget id — drives the delete-vs-archive decision.</summary>
    public readonly Dictionary<Guid, int> OrderLines = new();

    public Task<Widget?> GetByIdAsync(Guid id, CancellationToken ct)
        => Task.FromResult(Store.TryGetValue(id, out var w) ? w : null);

    public Task<Widget?> GetBySkuAsync(string normalizedSku, CancellationToken ct)
        => Task.FromResult(Store.Values.FirstOrDefault(w => w.Sku == normalizedSku));

    public Task<IReadOnlyList<Widget>> SearchAsync(WidgetQuery query, CancellationToken ct)
    {
        var items = Filter(query)
            .OrderBy(w => w.Name, StringComparer.Ordinal)
            .Skip(query.Offset)
            .Take(query.PageSize)
            .ToList();
        return Task.FromResult<IReadOnlyList<Widget>>(items);
    }

    public Task<int> CountAsync(WidgetQuery query, CancellationToken ct)
        => Task.FromResult(Filter(query).Count());

    public Task AddAsync(Widget widget, CancellationToken ct)
    {
        Store[widget.Id] = widget;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Widget widget, CancellationToken ct)
    {
        Store[widget.Id] = widget;
        return Task.CompletedTask;
    }

    public Task<int> CountOrderLinesAsync(Guid widgetId, CancellationToken ct)
        => Task.FromResult(OrderLines.TryGetValue(widgetId, out var n) ? n : 0);

    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        Store.Remove(id);
        return Task.CompletedTask;
    }

    private IEnumerable<Widget> Filter(WidgetQuery query)
    {
        IEnumerable<Widget> q = Store.Values.Where(w => !w.IsArchived);
        if (query.ActiveOnly)
        {
            q = q.Where(w => w.IsActive);
        }

        if (query.Search is not null)
        {
            q = q.Where(w =>
                w.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                w.Sku.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        }

        return q;
    }
}

public sealed class InMemoryCartRepository : ICartRepository
{
    public readonly Dictionary<Guid, Cart> Store = new();

    public Task<Cart?> GetAsync(Guid cartId, CancellationToken ct)
        => Task.FromResult(Store.TryGetValue(cartId, out var c) ? Clone(c) : null);

    public Task<Cart?> GetByUserAsync(Guid userId, CancellationToken ct)
        => Task.FromResult(Store.Values.Where(c => c.UserId == userId).Select(Clone).FirstOrDefault());

    public Task<Cart> CreateAsync(Guid? userId, CancellationToken ct)
    {
        var cart = new Cart { Id = Guid.NewGuid(), UserId = userId };
        Store[cart.Id] = cart;
        return Task.FromResult(Clone(cart));
    }

    public Task UpsertItemAsync(Guid cartId, Guid widgetId, int quantity, DateTimeOffset now, CancellationToken ct)
    {
        var cart = Store[cartId];
        var item = cart.Items.FirstOrDefault(i => i.WidgetId == widgetId);
        if (item is null)
        {
            cart.Items.Add(new CartItem { Id = Guid.NewGuid(), CartId = cartId, WidgetId = widgetId, Quantity = quantity, AddedAt = now });
        }
        else
        {
            item.Quantity = quantity;
        }

        return Task.CompletedTask;
    }

    public Task RemoveItemAsync(Guid cartId, Guid widgetId, CancellationToken ct)
    {
        if (Store.TryGetValue(cartId, out var cart))
        {
            cart.Items.RemoveAll(i => i.WidgetId == widgetId);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid cartId, CancellationToken ct)
    {
        Store.Remove(cartId);
        return Task.CompletedTask;
    }

    public Task TouchAsync(Guid cartId, DateTimeOffset now, CancellationToken ct)
    {
        // Mirrors the real repository, which stamps updated_at -- a no-op here would let a handler
        // forget to touch the cart and still pass.
        if (Store.TryGetValue(cartId, out var cart))
        {
            cart.UpdatedAt = now;
        }

        return Task.CompletedTask;
    }

    private static Cart Clone(Cart c) => new()
    {
        Id = c.Id,
        UserId = c.UserId,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
        Items = c.Items
            .Select(i => new CartItem { Id = i.Id, CartId = i.CartId, WidgetId = i.WidgetId, Quantity = i.Quantity, AddedAt = i.AddedAt })
            .ToList(),
    };
}

public sealed class InMemoryOrderRepository(InMemoryWidgetRepository widgets) : IOrderRepository
{
    public readonly List<Order> Orders = new();

    public Task<bool> TryPlaceAsync(Order order, CancellationToken ct)
    {
        foreach (var item in order.Items)
        {
            if (!widgets.Store.TryGetValue(item.WidgetId, out var w) || (w.QuantityOnHand - w.QuantityReserved) < item.Quantity)
            {
                return Task.FromResult(false);
            }
        }

        foreach (var item in order.Items)
        {
            widgets.Store[item.WidgetId].QuantityReserved += item.Quantity;
        }

        Orders.Add(order);
        return Task.FromResult(true);
    }

    public Task<bool> MarkAwaitingPaymentAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
    {
        var order = Orders.First(o => o.Id == orderId);
        if (order.Status != OrderStatus.Pending)
        {
            return Task.FromResult(false);
        }

        order.Status = OrderStatus.AwaitingPayment;
        order.PaymentProvider = provider;
        order.PaymentReference = reference;
        order.UpdatedAt = now;
        return Task.FromResult(true);
    }

    public Task<bool> MarkPaidAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
    {
        var order = Orders.First(o => o.Id == orderId);
        if (!AwaitingSettlement(order.Status))
        {
            return Task.FromResult(false);
        }

        order.Status = OrderStatus.Paid;
        order.PaymentProvider = provider;
        order.PaymentReference = reference;
        order.UpdatedAt = now;
        return Task.FromResult(true);
    }

    public Task<bool> MarkPaymentFailedAsync(Order order, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var stored = Orders.First(o => o.Id == order.Id);

        // Mirrors the repository's compare-and-set: a second delivery of the same failure is
        // declined, so the reservation is released exactly once.
        if (!AwaitingSettlement(stored.Status))
        {
            return Task.FromResult(false);
        }

        stored.Status = OrderStatus.PaymentFailed;
        stored.UpdatedAt = now;
        foreach (var item in order.Items)
        {
            if (widgets.Store.TryGetValue(item.WidgetId, out var w))
            {
                w.QuantityReserved -= item.Quantity;
            }
        }

        return Task.FromResult(true);
    }

    private static bool AwaitingSettlement(string status)
        => status is OrderStatus.Pending or OrderStatus.AwaitingPayment;

    public Task UpdateStatusAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        var stored = Orders.First(o => o.Id == order.Id);
        stored.Status = order.Status;
        stored.TrackingNumber = order.TrackingNumber;
        stored.UpdatedAt = now;

        // Mirrors OrderRepository: shipping converts the reservation into a real
        // decrement, cancelling hands it back, delivery moves nothing.
        foreach (var item in order.Items)
        {
            if (!widgets.Store.TryGetValue(item.WidgetId, out var w))
            {
                continue;
            }

            if (order.Status == OrderStatus.Shipped)
            {
                w.QuantityOnHand -= item.Quantity;
                w.QuantityReserved -= item.Quantity;
            }
            else if (order.Status == OrderStatus.Cancelled)
            {
                w.QuantityReserved -= item.Quantity;
            }
        }

        return Task.CompletedTask;
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct)
        => Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));

    public Task<Order?> GetByPaymentReferenceAsync(string provider, string reference, CancellationToken ct)
        => Task.FromResult(Orders.FirstOrDefault(o => o.PaymentProvider == provider && o.PaymentReference == reference));

    public Task<Order?> GetByNumberAndEmailAsync(string orderNumber, string email, CancellationToken ct)
        => Task.FromResult(Orders.FirstOrDefault(o =>
            o.OrderNumber == orderNumber && string.Equals(o.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Order>> GetForUserAsync(Guid userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Order>>(Orders.Where(o => o.UserId == userId).OrderByDescending(o => o.CreatedAt).ToList());

    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Order>>(Orders.OrderByDescending(o => o.CreatedAt).Take(limit).ToList());
}

public sealed class InMemoryPasswordResetTokenRepository : IPasswordResetTokenRepository
{
    public readonly List<PasswordResetToken> Tokens = new();

    public Task AddAsync(PasswordResetToken token, CancellationToken ct)
    {
        Tokens.Add(token);
        return Task.CompletedTask;
    }

    public Task<PasswordResetToken?> GetByHashAsync(string tokenHash, CancellationToken ct)
        => Task.FromResult(Tokens.FirstOrDefault(t => t.TokenHash == tokenHash));

    public Task MarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var token = Tokens.FirstOrDefault(t => t.Id == id);
        if (token is not null)
        {
            token.UsedAt = now;
        }

        return Task.CompletedTask;
    }

    public Task InvalidateForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var token in Tokens.Where(t => t.UserId == userId && t.UsedAt is null))
        {
            token.UsedAt = now;
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeSecureTokenGenerator : ISecureTokenGenerator
{
    private int _n;

    public string Last { get; private set; } = string.Empty;

    public string Generate()
    {
        Last = "reset-token-" + (++_n);
        return Last;
    }

    public string Hash(string rawToken) => "hash:" + rawToken;
}

public sealed class FakeEmailSender : IEmailSender
{
    public readonly List<EmailMessage> Sent = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryRefreshTokenRepository : IRefreshTokenRepository
{
    public readonly List<RefreshToken> Tokens = new();

    public Task AddAsync(RefreshToken token, CancellationToken ct)
    {
        Tokens.Add(token);
        return Task.CompletedTask;
    }

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct)
        => Task.FromResult(Tokens.FirstOrDefault(t => t.TokenHash == tokenHash));

    public Task UpdateAsync(RefreshToken token, CancellationToken ct) => Task.CompletedTask;

    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        foreach (var t in Tokens.Where(t => t.FamilyId == familyId && t.RevokedAt is null))
        {
            t.RevokedAt = revokedAt;
        }

        return Task.CompletedTask;
    }

    public Task RevokeAllForUserAsync(Guid userId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        foreach (var t in Tokens.Where(t => t.UserId == userId && t.RevokedAt is null))
        {
            t.RevokedAt = revokedAt;
        }

        return Task.CompletedTask;
    }
}

public sealed class RecordingAuditLog : IAuditLog
{
    public readonly List<string> Actions = new();

    public Task WriteAsync(Guid? userId, string action, string? detail, CancellationToken ct)
    {
        Actions.Add(action);
        return Task.CompletedTask;
    }
}

public sealed class FakePasswordHasher : IPasswordHasher
{
    public string Hash(string password) => "hash:" + password;

    public bool Verify(string password, string hash) => hash == "hash:" + password;
}

public sealed class StubTokenService : ITokenService
{
    private static readonly DateTimeOffset Far = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public AccessToken CreateAccessToken(User user) => new("access-token", Far);

    public IssuedRefreshToken CreateRefreshToken(Guid familyId) => new("refresh-raw", "refresh-hash-" + familyId, familyId, Far);

    public string HashRefreshToken(string rawToken) => "hash:" + rawToken;

    public string CreateChallengeToken(User user) => "challenge-" + user.Id;

    public Task<Guid?> ValidateChallengeTokenAsync(string challengeToken)
    {
        var raw = challengeToken.StartsWith("challenge-", StringComparison.Ordinal)
            ? challengeToken["challenge-".Length..]
            : challengeToken;
        return Task.FromResult(Guid.TryParse(raw, out var id) ? (Guid?)id : null);
    }
}

public sealed class FakeTotpService : ITotpService
{
    public string ValidCode { get; set; } = "654321";

    public TotpSecret CreateSecret(string accountName) => new("SECRETBASE32", "otpauth://totp/x");

    public bool Verify(string secretBase32, string code, DateTimeOffset now) => code == ValidCode;
}

public sealed class InMemoryTwoFactorRepository : ITwoFactorRepository
{
    public readonly Dictionary<Guid, TwoFactorSecret> Secrets = new();
    private readonly List<RecoveryRow> _codes = new();

    public Task UpsertPendingSecretAsync(Guid userId, string secretBase32, CancellationToken ct)
    {
        Secrets[userId] = new TwoFactorSecret { UserId = userId, Secret = secretBase32, IsConfirmed = false };
        return Task.CompletedTask;
    }

    public Task<TwoFactorSecret?> GetSecretAsync(Guid userId, CancellationToken ct)
        => Task.FromResult(Secrets.TryGetValue(userId, out var s) ? s : null);

    public Task MarkConfirmedAsync(Guid userId, CancellationToken ct)
    {
        if (Secrets.TryGetValue(userId, out var s))
        {
            s.IsConfirmed = true;
        }

        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(Guid userId, CancellationToken ct)
    {
        Secrets.Remove(userId);
        return Task.CompletedTask;
    }

    public Task AddRecoveryCodesAsync(Guid userId, IReadOnlyList<string> codeHashes, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var hash in codeHashes)
        {
            _codes.Add(new RecoveryRow { UserId = userId, Hash = hash });
        }

        return Task.CompletedTask;
    }

    public Task DeleteRecoveryCodesAsync(Guid userId, CancellationToken ct)
    {
        _codes.RemoveAll(c => c.UserId == userId);
        return Task.CompletedTask;
    }

    public Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string codeHash, DateTimeOffset usedAt, CancellationToken ct)
    {
        var row = _codes.FirstOrDefault(c => c.UserId == userId && c.Hash == codeHash && c.UsedAt is null);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.UsedAt = usedAt;
        return Task.FromResult(true);
    }

    private sealed class RecoveryRow
    {
        public Guid UserId { get; set; }

        public string Hash { get; set; } = string.Empty;

        public DateTimeOffset? UsedAt { get; set; }
    }
}

/// <summary>
/// Captures what a handler logged, so a swallowed failure can be asserted to
/// have left a trace. The handlers deliberately do not fail an order, an
/// account or a transition when a notification errors; the log is the only
/// thing that distinguishes that from nothing having happened.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public readonly List<(LogLevel Level, string Message, Exception? Error)> Entries = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));
}
