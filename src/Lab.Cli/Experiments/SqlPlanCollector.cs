using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataPerformanceLab.Cli.Experiments;

public sealed record CapturedParameter(string Name, string DbType, int Size, string Value);

public sealed record CapturedCommand(string CommandText, IReadOnlyList<CapturedParameter> Parameters);

/// <summary>
/// Records the SQL EF Core actually sent, with parameter types and values, so the plan
/// collector can replay the real statement instead of a hand-written approximation.
/// </summary>
public sealed class CommandCaptureInterceptor : DbCommandInterceptor
{
    public CapturedCommand? LastCommand { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbCommand command)
    {
        var parameters = new List<CapturedParameter>(command.Parameters.Count);
        foreach (DbParameter parameter in command.Parameters)
        {
            parameters.Add(new CapturedParameter(
                parameter.ParameterName,
                parameter.DbType.ToString(),
                parameter.Size,
                Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        LastCommand = new CapturedCommand(command.CommandText, parameters);
    }
}

public sealed record PlanCollectionResult(string PlanXml, string StatisticsText, int RowCount);

/// <summary>
/// Replays a captured statement with SET STATISTICS IO/TIME/XML on. This runs in a separate,
/// explicitly instrumented pass: the protocol keeps plan-collection overhead out of the timed samples.
/// </summary>
public sealed class SqlPlanCollector(string connectionString)
{
    public async Task<PlanCollectionResult> CollectAsync(CapturedCommand captured, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, e) => messages.AppendLine(e.Message);

        await connection.OpenAsync(ct);

        await ExecuteNonQueryAsync(connection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;", ct);

        // A first uninstrumented execution warms the buffer pool so the IO numbers describe
        // steady-state reads rather than the very first physical read of the table.
        await ReadAllAsync(connection, captured, collectPlan: false, ct);

        messages.Clear();
        await ExecuteNonQueryAsync(connection, "SET STATISTICS XML ON;", ct);
        var (planXml, rowCount) = await ReadAllAsync(connection, captured, collectPlan: true, ct);
        await ExecuteNonQueryAsync(connection, "SET STATISTICS XML OFF;", ct);

        if (string.IsNullOrWhiteSpace(planXml))
        {
            throw new InvalidOperationException(
                "SET STATISTICS XML returned no showplan result set for the captured command.");
        }

        return new PlanCollectionResult(planXml, messages.ToString(), rowCount);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string? PlanXml, int RowCount)> ReadAllAsync(
        SqlConnection connection, CapturedCommand captured, bool collectPlan, CancellationToken ct)
    {
        await using var command = new SqlCommand(captured.CommandText, connection) { CommandTimeout = 300 };
        foreach (var parameter in captured.Parameters)
        {
            command.Parameters.Add(ToSqlParameter(parameter));
        }

        string? planXml = null;
        var rowCount = 0;

        await using var reader = await command.ExecuteReaderAsync(ct);
        do
        {
            while (await reader.ReadAsync(ct))
            {
                if (collectPlan
                    && reader.FieldCount == 1
                    && reader.GetFieldType(0) == typeof(string)
                    && reader.GetValue(0) is string value
                    && value.StartsWith("<ShowPlanXML", StringComparison.Ordinal))
                {
                    planXml = value;
                    continue;
                }

                rowCount++;
            }
        }
        while (await reader.NextResultAsync(ct));

        return (planXml, rowCount);
    }

    private static SqlParameter ToSqlParameter(CapturedParameter captured)
    {
        var dbType = Enum.Parse<DbType>(captured.DbType);
        var parameter = new SqlParameter(captured.Name, ConvertValue(dbType, captured.Value))
        {
            DbType = dbType
        };

        if (captured.Size != 0)
        {
            parameter.Size = captured.Size;
        }

        return parameter;
    }

    private static object ConvertValue(DbType dbType, string value) => dbType switch
    {
        DbType.Int32 => int.Parse(value, CultureInfo.InvariantCulture),
        DbType.Int64 => long.Parse(value, CultureInfo.InvariantCulture),
        DbType.Boolean => bool.Parse(value),
        DbType.Decimal => decimal.Parse(value, CultureInfo.InvariantCulture),
        DbType.DateTime or DbType.DateTime2 => DateTime.Parse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => value
    };

    /// <summary>Renders the captured statement with its parameters as a runnable .sql file.</summary>
    public static string FormatSqlFile(CapturedCommand captured)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- Captured from EF Core, including the parameter types and values it sent.");
        builder.AppendLine("-- Synthetic lab data only; no connection information is recorded here.");
        foreach (var parameter in captured.Parameters)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"-- parameter {parameter.Name} ({parameter.DbType}, size {parameter.Size}) = {parameter.Value}");
        }

        builder.AppendLine();
        builder.AppendLine(captured.CommandText);
        return builder.ToString();
    }
}
