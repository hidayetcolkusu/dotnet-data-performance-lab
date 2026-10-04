namespace DataPerformanceLab.Configuration;

public sealed class LabDataOptions
{
    public const string SectionName = "Data";

    public const string ApiDatabaseName = "DataPerformanceLab";
    public const string ExperimentDatabaseName = "DataPerformanceLab_Experiment";
    public const string TestDatabasePrefix = "DataPerformanceLab_Test_";

    public static readonly string[] AllowedLabDatabaseNames = [ApiDatabaseName, ExperimentDatabaseName];

    public string DatabaseName { get; init; } = ApiDatabaseName;

    public SqlServerOptions SqlServer { get; init; } = new();

    public RedisOptions Redis { get; init; } = new();

    public sealed class SqlServerOptions
    {
        public string Host { get; init; } = "127.0.0.1";

        public int Port { get; init; } = 1433;

        public string UserId { get; init; } = "sa";

        public string PasswordEnvironmentVariable { get; init; } = "MSSQL_SA_PASSWORD";

        public string? PasswordOverride { get; init; }

        public string ResolvePassword() =>
            PasswordOverride
            ?? Environment.GetEnvironmentVariable(PasswordEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"SQL password is not available. Set the '{PasswordEnvironmentVariable}' environment variable "
                + "(see .env.example and scripts/bootstrap.ps1).");

        public string GetConnectionString(string databaseName) =>
            $"Server={Host},{Port};Database={databaseName};User ID={UserId};Password={ResolvePassword()}"
            + ";Encrypt=True;TrustServerCertificate=True";

        public string GetMasterConnectionString() =>
            $"Server={Host},{Port};Database=master;User ID={UserId};Password={ResolvePassword()}"
            + ";Encrypt=True;TrustServerCertificate=True";
    }

    public sealed class RedisOptions
    {
        public string Host { get; init; } = "127.0.0.1";

        public int Port { get; init; } = 6379;
    }
}
