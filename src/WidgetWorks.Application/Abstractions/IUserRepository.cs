using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct);

    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<User?> GetByGoogleSubAsync(string googleSub, CancellationToken ct);

    Task AddAsync(User user, CancellationToken ct);

    Task UpdateAsync(User user, CancellationToken ct);

    Task<Guid?> GetSecurityStampAsync(Guid userId, CancellationToken ct);
}
