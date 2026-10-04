using Microsoft.Extensions.Hosting;

namespace DataPerformanceLab.Import;

/// <summary>
/// Raised by the fault injector. It is deliberately its own type so a test can tell an
/// injected failure apart from a real infrastructure error.
/// </summary>
public sealed class InjectedImportFaultException(string message) : Exception(message);

/// <summary>
/// Simulates a process dying mid-import so the recovery contract can be proven rather than
/// asserted. It is a no-op in every normal run: it can only be armed from the Testing
/// environment or from an explicit CLI experiment flag.
/// </summary>
public sealed class ImportFaultInjector
{
    /// <summary>The instance used by every ordinary import; it never throws.</summary>
    public static ImportFaultInjector Disabled { get; } = new();

    private ImportFaultInjector()
    {
    }

    /// <summary>Fail inside the transaction of this 1-based batch, before it commits.</summary>
    public int? FailBeforeCommitOnBatch { get; private init; }

    /// <summary>
    /// Fail after this 1-based batch has committed but before the caller is told — the case
    /// where the commit reply is lost and resume must not double-count anything.
    /// </summary>
    public int? FailAfterCommitOnBatch { get; private init; }

    public bool IsArmed => FailBeforeCommitOnBatch is not null || FailAfterCommitOnBatch is not null;

    /// <summary>
    /// Arms the injector. Throws outside the Testing environment, so an armed injector can
    /// never be reachable from a normal Development run of the API or CLI.
    /// </summary>
    public static ImportFaultInjector ForExperiment(
        IHostEnvironment environment, int? failBeforeCommitOnBatch, int? failAfterCommitOnBatch)
    {
        if (!environment.IsEnvironment(LabEnvironment.Testing))
        {
            throw new InvalidOperationException(
                "Import fault injection is only available in the Testing environment.");
        }

        return new ImportFaultInjector
        {
            FailBeforeCommitOnBatch = failBeforeCommitOnBatch,
            FailAfterCommitOnBatch = failAfterCommitOnBatch
        };
    }

    /// <summary>Test-only factory used by the integration tests, which already run as Testing.</summary>
    public static ImportFaultInjector BeforeCommitOnBatch(int batchNumber) =>
        new() { FailBeforeCommitOnBatch = batchNumber };

    public static ImportFaultInjector AfterCommitOnBatch(int batchNumber) =>
        new() { FailAfterCommitOnBatch = batchNumber };

    public void ThrowIfFailingBeforeCommit(int batchNumber)
    {
        if (FailBeforeCommitOnBatch == batchNumber)
        {
            throw new InjectedImportFaultException(
                $"Injected failure inside batch {batchNumber} before it committed.");
        }
    }

    public void ThrowIfFailingAfterCommit(int batchNumber)
    {
        if (FailAfterCommitOnBatch == batchNumber)
        {
            throw new InjectedImportFaultException(
                $"Injected failure after batch {batchNumber} committed, before the caller was told.");
        }
    }
}
