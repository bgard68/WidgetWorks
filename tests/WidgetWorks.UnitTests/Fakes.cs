using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
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
}
