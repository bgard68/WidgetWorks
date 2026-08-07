using System.Data;
using Npgsql;

namespace WidgetWorks.Infrastructure.Persistence;

public interface IDbConnectionFactory
{
    Task<IDbConnection> OpenAsync(CancellationToken ct);
}

public sealed class NpgsqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public async Task<IDbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
