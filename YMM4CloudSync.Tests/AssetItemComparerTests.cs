using Xunit;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.Core.ViewModels;

namespace YMM4CloudSync.Tests;

public class AssetItemComparerTests
{
    private static readonly DateTime Base = new(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);

    private static AssetItemViewModel Folder(string name)
        => new(new CloudFile(name, name, CloudMimeTypes.DropboxFolder, null, Base), "", AssetState.NotDownloaded);

    private static AssetItemViewModel File(string name, long size = 100, int minutes = 0,
        AssetState state = AssetState.NotDownloaded)
        => new(new CloudFile(name, name, "application/octet-stream", size, Base.AddMinutes(minutes)), "", state);

    private static List<string> Sort(AssetSortKey key, bool descending, params AssetItemViewModel[] items)
    {
        var comparer = new AssetItemComparer(key, descending);
        var list = items.ToList();

        list.Sort((a, b) => comparer.Compare(a, b));

        return list.Select(i => i.Name).ToList();
    }

    [Fact]
    public void FoldersComeFirst_WhateverTheSortKey()
    {
        var order = Sort(AssetSortKey.Size, false, File("a.png"), Folder("zzz"), File("b.png"));

        Assert.Equal("zzz", order[0]);
    }

    [Fact]
    public void FoldersStayFirst_EvenWhenDescending()
    {
        var order = Sort(AssetSortKey.Name, true, File("a.png"), Folder("zzz"), File("b.png"));

        Assert.Equal("zzz", order[0]);
    }

    [Fact]
    public void SortsByNameNaturally()
    {
        var order = Sort(AssetSortKey.Name, false, File("a10.png"), File("a2.png"), File("a1.png"));

        Assert.Equal(["a1.png", "a2.png", "a10.png"], order);
    }

    [Fact]
    public void SortsBySize()
    {
        var order = Sort(AssetSortKey.Size, false, File("big", 900), File("small", 10), File("mid", 100));

        Assert.Equal(["small", "mid", "big"], order);
    }

    [Fact]
    public void SortsByModifiedTime()
    {
        var order = Sort(AssetSortKey.ModifiedTime, false,
            File("late", minutes: 10), File("early", minutes: -10), File("mid"));

        Assert.Equal(["early", "mid", "late"], order);
    }

    [Fact]
    public void SortsByState()
    {
        var order = Sort(AssetSortKey.State, false,
            File("done", state: AssetState.Downloaded),
            File("missing", state: AssetState.NotDownloaded));

        Assert.Equal(["missing", "done"], order);
    }

    [Fact]
    public void FallsBackToTheNameWhenTheKeyTies()
    {
        var order = Sort(AssetSortKey.Size, false, File("b.png"), File("a.png"));

        Assert.Equal(["a.png", "b.png"], order);
    }

    [Fact]
    public void DescendingReversesTheOrder()
    {
        var order = Sort(AssetSortKey.Size, true, File("big", 900), File("small", 10));

        Assert.Equal(["big", "small"], order);
    }

    [Fact]
    public void SortsByType()
    {
        var order = Sort(AssetSortKey.Type, false, File("photo.png"), File("bgm.wav"), File("clip.mp4"));

        Assert.Equal(["clip.mp4", "photo.png", "bgm.wav"], order);
    }
}
