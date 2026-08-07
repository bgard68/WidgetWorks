using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.UnitTests.Fakes;

public sealed class InMemoryUserRepository : IUserRepository
{
    public readonly Dictionary<Guid, User> Store = new();

    public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct)
        => Task.FromResult(Store.Values.FirstOrDefault(u => u.NormalizedEmail == normalizedEmail));

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct)
        => Task.FromResult(Store.TryGetValue(id, out var u) ? u : null);

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

    private IEnumerable<Widget> Filter(WidgetQuery query)
    {
        IEnumerable<Widget> q = Store.Values;
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
