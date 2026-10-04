using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// The tests and the local lab must run the same container images. Both pin by digest; this
/// fails when one side is bumped and the other is not.
/// </summary>
public sealed class ImagePinTests
{
    [Theory]
    [InlineData(SqlServerFixture.Image)]
    [InlineData(RedisFixture.Image)]
    public void TheFixtureImageIsPinnedToTheDigestComposeUses(string image)
    {
        Assert.Contains("@sha256:", image);

        var compose = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "compose.yaml"));
        Assert.Contains($"image: {image}", compose);
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "compose.yaml")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("compose.yaml was not found above the test output directory.");
    }
}
