namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// The integration suite runs against real SQL Server and Redis containers. If Docker is not
/// reachable the tests <b>fail</b> with a plain explanation; they are never skipped.
///
/// A skipped test reports green, and a green run that exercised no database is exactly the
/// false assurance this project exists to avoid.
/// </summary>
internal static class DockerRequirement
{
    /// <summary>
    /// Starts a fixture's container, translating an unreachable Docker engine into a message
    /// that says what to do. Wrapping the start — rather than probing Testcontainers' own
    /// settings — keeps this guard working across Testcontainers versions.
    /// </summary>
    public static async Task StartAsync(string containerDescription, Func<Task> start)
    {
        try
        {
            await start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Could not start the {containerDescription} container, so the integration tests "
                + "cannot run against a real database. Start the Docker engine and re-run. These "
                + "tests are deliberately not skippable: a green run that touched no database "
                + $"would prove nothing. Underlying failure: {ex.Message}",
                ex);
        }
    }
}
