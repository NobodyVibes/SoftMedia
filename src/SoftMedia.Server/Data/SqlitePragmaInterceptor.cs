using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SoftMedia.Server.Data;

/// <summary>
/// SR-WI-035 — assert the SQLite operating pragmas on every connection open. WAL was
/// previously active only because EF Core happened to set it when it CREATED the database
/// file; a DB supplied or repaired by an outside tool in rollback-journal mode would have
/// silently run degraded (writer blocks readers). busy_timeout was never set explicitly
/// (the provider's 30s command timeout merely acted like one). Making all three explicit
/// removes the "works by accident" dependency.
/// </summary>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA busy_timeout=30000;" +
        "PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        // Guard on provider: the InMemory test provider also raises these events.
        if (!connection.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }
}
