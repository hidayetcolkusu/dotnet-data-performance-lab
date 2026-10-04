using DataPerformanceLab.Cli;
using Xunit;

namespace DataPerformanceLab.UnitTests;

public sealed class CliArgumentsTests
{
    private static readonly string[] Flags = ["reset", "skip-plans", "help"];

    [Fact]
    public void ADeclaredFlagIsAbsentUntilItIsActuallyPassed()
    {
        // The destructive commands gate on this. Treating a *declared* flag as a *passed*
        // one made `db migrate --database X` drop the database nobody asked to drop.
        var args = new CliArguments(["--database", "DataPerformanceLab"], Flags);

        Assert.False(args.HasFlag("reset"));
        Assert.False(args.HasFlag("skip-plans"));
        Assert.False(args.HasFlag("help"));
        Assert.Equal("DataPerformanceLab", args.GetOption("database"));
    }

    [Fact]
    public void NoFlagIsSetForEmptyArguments()
    {
        var args = new CliArguments([], Flags);

        Assert.All(Flags, flag => Assert.False(args.HasFlag(flag)));
    }

    [Fact]
    public void APassedFlagIsSetAndTheOthersStayUnset()
    {
        var args = new CliArguments(["--database", "DataPerformanceLab", "--reset"], Flags);

        Assert.True(args.HasFlag("reset"));
        Assert.False(args.HasFlag("skip-plans"));
    }

    [Fact]
    public void FlagsAreCaseInsensitiveInBothDirections()
    {
        var args = new CliArguments(["--RESET"], Flags);

        Assert.True(args.HasFlag("reset"));
        Assert.True(args.HasFlag("Reset"));
    }

    [Fact]
    public void AFlagConsumesNoValue()
    {
        var args = new CliArguments(["--reset", "--database", "DataPerformanceLab"], Flags);

        Assert.True(args.HasFlag("reset"));
        Assert.Equal("DataPerformanceLab", args.GetOption("database"));
    }

    [Fact]
    public void AnUndeclaredFlagNameIsNeverReportedAsPresent()
    {
        var args = new CliArguments(["--reset"], Flags);

        Assert.False(args.HasFlag("force"));
    }

    [Fact]
    public void OptionsAreReadBackByNameAndMissingOnesAreNull()
    {
        var args = new CliArguments(["--file", "catalog.csv", "--batch-size", "500"], Flags);

        Assert.Equal("catalog.csv", args.GetOption("file"));
        Assert.Equal("500", args.GetOption("batch-size"));
        Assert.Null(args.GetOption("output"));
    }

    [Fact]
    public void TheLastValueWinsWhenAnOptionIsRepeated()
    {
        var args = new CliArguments(["--category", "1", "--category", "7"], Flags);

        Assert.Equal("7", args.GetOption("category"));
    }

    [Fact]
    public void PositionalTokensAreKeptInOrder()
    {
        var args = new CliArguments(["migrate", "extra", "--reset"], Flags);

        Assert.Equal(["migrate", "extra"], args.Positional);
    }

    [Fact]
    public void AnOptionWithNoValueIsRejectedRatherThanSilentlyEmpty()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CliArguments(["--database"], Flags));

        Assert.Contains("--database", ex.Message);
    }

    [Fact]
    public void AnOptionFollowedByAnotherOptionIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CliArguments(["--database", "--reset"], Flags));
    }

    [Fact]
    public void AnEmptyOptionNameIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CliArguments(["--", "value"], Flags));
    }
}
