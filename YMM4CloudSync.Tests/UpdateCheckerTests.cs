using Xunit;
using YMM4CloudSync.Core.Commons.Network;

namespace YMM4CloudSync.Tests;

public class UpdateCheckerTests
{
    private static GitHubRelease Release(string tag, bool prerelease = false, bool draft = false) => new()
    {
        TagName = tag,
        Body = $"body of {tag}",
        HtmlUrl = $"https://example.invalid/{tag}",
        Prerelease = prerelease,
        Draft = draft
    };

    [Fact]
    public void SelectUpdate_NotifiesForPrereleaseFlaggedReleases()
    {
        var releases = new[] { Release("v0.3.0", prerelease: true) };

        var result = UpdateChecker.SelectUpdate(releases, new Version(0, 2, 0));

        Assert.NotNull(result);
        Assert.Equal("v0.3.0", result.TagName);
        Assert.Equal(new Version(0, 3, 0), result.Version);
    }

    [Fact]
    public void SelectUpdate_IgnoresDrafts()
    {
        var releases = new[] { Release("v0.9.0", prerelease: true, draft: true) };

        Assert.Null(UpdateChecker.SelectUpdate(releases, new Version(0, 2, 0)));
    }

    [Fact]
    public void SelectUpdate_ReturnsNull_WhenCurrentVersionIsUpToDate()
    {
        var releases = new[] { Release("v0.3.0", prerelease: true) };

        Assert.Null(UpdateChecker.SelectUpdate(releases, new Version(0, 3, 0)));
    }

    [Fact]
    public void SelectUpdate_ReturnsNull_WhenCurrentVersionIsNewer()
    {
        var releases = new[] { Release("v0.3.0", prerelease: true) };

        Assert.Null(UpdateChecker.SelectUpdate(releases, new Version(0, 4, 0)));
    }

    [Fact]
    public void SelectUpdate_PicksHighestVersion_NotListOrder()
    {
        var releases = new[]
        {
            Release("v0.2.9", prerelease: true),
            Release("v0.10.0", prerelease: true),
            Release("v0.9.0", prerelease: true)
        };

        var result = UpdateChecker.SelectUpdate(releases, new Version(0, 2, 0));

        Assert.NotNull(result);
        Assert.Equal("v0.10.0", result.TagName);
    }

    [Theory]
    [InlineData("v0.3.0")]
    [InlineData("V0.3.0")]
    [InlineData("0.3.0")]
    [InlineData("v0.3.0-rc1")]
    [InlineData("v0.3.0+build5")]
    [InlineData(" v0.3.0 ")]
    public void SelectUpdate_ParsesCommonTagShapes(string tag)
    {
        var result = UpdateChecker.SelectUpdate([Release(tag, prerelease: true)], new Version(0, 2, 0));

        Assert.NotNull(result);
        Assert.Equal(new Version(0, 3, 0), result.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    public void SelectUpdate_SkipsUnparsableTags(string tag)
    {
        Assert.Null(UpdateChecker.SelectUpdate([Release(tag, prerelease: true)], new Version(0, 2, 0)));
    }

    [Fact]
    public void SelectUpdate_SkipsUnparsableTags_ButStillFindsValidOnes()
    {
        var releases = new[]
        {
            Release("nightly", prerelease: true),
            Release("v0.3.0", prerelease: true)
        };

        var result = UpdateChecker.SelectUpdate(releases, new Version(0, 2, 0));

        Assert.NotNull(result);
        Assert.Equal("v0.3.0", result.TagName);
    }

    [Fact]
    public void SelectUpdate_ReturnsNull_ForEmptyList()
    {
        Assert.Null(UpdateChecker.SelectUpdate([], new Version(0, 2, 0)));
    }
}
