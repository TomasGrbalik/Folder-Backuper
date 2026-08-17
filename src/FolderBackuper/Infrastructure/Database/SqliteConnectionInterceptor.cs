using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FolderBackuper.Infrastructure.Database;

internal sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    private const int BusyTimeoutMilliseconds = 5_000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
