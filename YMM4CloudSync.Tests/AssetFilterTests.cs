using Xunit;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Tests;

public class AssetFilterTests
{
    private static bool MatchesFile(AssetFilterCriteria criteria, string name,
        AssetCategory category = AssetCategory.Video, AssetState state = AssetState.Downloaded)
        => AssetFilter.Matches(criteria, name, false, category, state);

    private static bool MatchesFolder(AssetFilterCriteria criteria, string name = "素材")
        => AssetFilter.Matches(criteria, name, true, AssetCategory.Folder, AssetState.NotDownloaded);

    [Fact]
    public void DefaultCriteria_MatchEverything()
    {
        Assert.True(MatchesFile(AssetFilterCriteria.None, "clip.mp4"));
        Assert.True(MatchesFolder(AssetFilterCriteria.None));
    }

    [Fact]
    public void DefaultCriteria_AreNotConsideredFiltered()
    {
        Assert.False(AssetFilterCriteria.None.IsFiltered);
        Assert.False(AssetFilterCriteria.None.IsFilteredByType);
    }

    [Theory]
    [InlineData("背景", true)]
    [InlineData("森", true)]
    [InlineData("はいけい", false)]
    public void Query_MatchesPartOfTheName(string query, bool expected)
    {
        var criteria = AssetFilterCriteria.None with { Query = query };

        Assert.Equal(expected, MatchesFile(criteria, "背景 森.png", AssetCategory.Image));
    }

    [Fact]
    public void Query_IsCaseInsensitive()
    {
        Assert.True(MatchesFile(AssetFilterCriteria.None with { Query = "CLIP" }, "clip.mp4"));
    }

    [Fact]
    public void Query_IgnoresSurroundingWhitespace()
    {
        Assert.True(MatchesFile(AssetFilterCriteria.None with { Query = "  clip  " }, "clip.mp4"));
    }

    [Fact]
    public void Query_AppliesToFoldersToo()
    {
        Assert.False(MatchesFolder(AssetFilterCriteria.None with { Query = "音声" }, "背景"));
        Assert.True(MatchesFolder(AssetFilterCriteria.None with { Query = "背景" }, "背景"));
    }

    [Fact]
    public void Query_AloneCountsAsFilteredButNotByType()
    {
        var criteria = AssetFilterCriteria.None with { Query = "a" };

        Assert.True(criteria.IsFiltered);
        Assert.False(criteria.IsFilteredByType);
    }

    [Theory]
    [InlineData(AssetCategory.Video)]
    [InlineData(AssetCategory.Audio)]
    [InlineData(AssetCategory.Image)]
    [InlineData(AssetCategory.Text)]
    [InlineData(AssetCategory.Other)]
    public void EachTypeCanBeHiddenIndependently(AssetCategory category)
    {
        var criteria = AssetFilterCriteria.None with
        {
            ShowVideo = category != AssetCategory.Video,
            ShowAudio = category != AssetCategory.Audio,
            ShowImage = category != AssetCategory.Image,
            ShowText = category != AssetCategory.Text,
            ShowOther = category != AssetCategory.Other
        };

        Assert.False(MatchesFile(criteria, "x", category));
        Assert.True(criteria.IsFilteredByType);
    }

    [Fact]
    public void HidingOneTypeKeepsTheOthersVisible()
    {
        var criteria = AssetFilterCriteria.None with { ShowVideo = false };

        Assert.False(MatchesFile(criteria, "clip.mp4"));
        Assert.True(MatchesFile(criteria, "bgm.wav", AssetCategory.Audio));
        Assert.True(MatchesFile(criteria, "photo.png", AssetCategory.Image));
    }

    [Fact]
    public void FoldersIgnoreTheFileTypeFilters()
    {
        var criteria = AssetFilterCriteria.None with { ShowVideo = false, ShowAudio = false, ShowOther = false };

        Assert.True(MatchesFolder(criteria));
    }

    [Fact]
    public void FoldersCanBeHiddenOnTheirOwn()
    {
        var criteria = AssetFilterCriteria.None with { ShowFolder = false };

        Assert.False(MatchesFolder(criteria));
        Assert.True(MatchesFile(criteria, "clip.mp4"));
    }

    [Fact]
    public void FoldersIgnoreTheDownloadedOnlyFilter()
    {
        Assert.True(MatchesFolder(AssetFilterCriteria.None with { DownloadedOnly = true }));
    }

    [Theory]
    [InlineData(AssetState.Downloaded, true)]
    [InlineData(AssetState.Stale, true)]
    [InlineData(AssetState.NotDownloaded, false)]
    [InlineData(AssetState.Downloading, false)]
    [InlineData(AssetState.Failed, false)]
    public void DownloadedOnly_KeepsLocallyUsableFiles(AssetState state, bool expected)
    {
        var criteria = AssetFilterCriteria.None with { DownloadedOnly = true };

        Assert.Equal(expected, MatchesFile(criteria, "clip.mp4", AssetCategory.Video, state));
    }
}
