using System.Data;
using Microsoft.Data.SqlClient;

namespace DataPerformanceLab.Import;

/// <summary>
/// Serializes every import run against one database with a session-owned
/// <c>sp_getapplock</c>. The lock is bound to a connection this object keeps open:
/// when the process dies the connection drops and SQL Server releases the lock, so a crashed
/// importer cannot block the next run forever.
///
/// This is a single-database lab lock. It is not a claim about distributed importers.
/// </summary>
public sealed class ImportLock : IAsyncDisposable
{
    public const string ResourceName = "DataPerformanceLab:import";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly SqlConnection _connection;

    private ImportLock(SqlConnection connection) => _connection = connection;

    /// <summary>
    /// Returns null when another importer holds the lock — the caller must then report
    /// <c>ImportBusy</c> and stop, never proceed unlocked.
    /// </summary>
    public static async Task<ImportLock?> TryAcquireAsync(
        string connectionString, TimeSpan timeout, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);

            await using var command = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = (int)timeout.TotalSeconds + 30
            };
            command.Parameters.AddWithValue("@Resource", ResourceName);
            command.Parameters.AddWithValue("@LockMode", "Exclusive");
            command.Parameters.AddWithValue("@LockOwner", "Session");
            command.Parameters.AddWithValue("@LockTimeout", (int)timeout.TotalMilliseconds);

            var returnValue = new SqlParameter
            {
                ParameterName = "@Result",
                SqlDbType = SqlDbType.Int,
                Direction = ParameterDirection.ReturnValue
            };
            command.Parameters.Add(returnValue);

            await command.ExecuteNonQueryAsync(ct);

            // 0 = granted, 1 = granted after waiting. Everything negative is a failure:
            // -1 timeout, -2 cancelled, -3 deadlock victim, -999 parameter/other error.
            var result = (int)returnValue.Value;
            if (result >= 0)
            {
                return new ImportLock(connection);
            }

            await connection.DisposeAsync();
            return result switch
            {
                -1 or -2 or -3 => null,
                _ => throw new InvalidOperationException(
                    $"sp_getapplock('{ResourceName}') failed with result {result}.")
            };
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
