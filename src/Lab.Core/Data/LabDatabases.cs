using DataPerformanceLab.Configuration;

namespace DataPerformanceLab.Data;

public static class LabDatabases
{
    public static string ResolveTarget(string? requested, bool allowTestDatabases)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return LabDataOptions.ApiDatabaseName;
        }

        if (LabDataOptions.AllowedLabDatabaseNames.Contains(requested, StringComparer.Ordinal))
        {
            return requested;
        }

        if (allowTestDatabases && requested.StartsWith(LabDataOptions.TestDatabasePrefix, StringComparison.Ordinal))
        {
            return requested;
        }

        throw new InvalidOperationException(
            $"Database '{requested}' is not an allowed lab database. " +
            $"Allowed: {string.Join(", ", LabDataOptions.AllowedLabDatabaseNames)}.");
    }
}
