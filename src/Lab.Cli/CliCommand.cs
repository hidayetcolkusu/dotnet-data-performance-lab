namespace DataPerformanceLab.Cli;

public interface ICliCommand
{
    string Name { get; }

    Task<int> ExecuteAsync(string[] args, CancellationToken ct);
}
