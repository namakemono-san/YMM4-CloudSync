using System.IO;
using Xunit;
using YMM4CloudSync.Core.Commons.Configuration;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Tests;

[Collection("AssetState")]
public sealed class AssetStateStoreTests : IDisposable
{
    private static readonly DateTime Modified = new(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);

    private readonly string _workDir;

    public AssetStateStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "assetstate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        AssetStateStore.PathOverride = Path.Combine(_workDir, "asset_state.json");
    }

    public void Dispose()
    {
        AssetStateStore.PathOverride = null;

        try { Directory.Delete(_workDir, true); } catch { }
    }

    private string CreateLocalFile(string name = "clip.mp4")
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, "x");

        return path;
    }

    private static AssetStateEntry NewEntry(string connectionKey, string fileId, string localPath, long? size = 100)
        => new()
        {
            ConnectionKey = connectionKey,
            FileId = fileId,
            RemoteModifiedTime = Modified,
            RemoteSize = size,
            LocalPath = localPath,
            RemoteParentId = "folder-1"
        };

    [Fact]
    public void SaveThenFind_ReturnsTheEntry()
    {
        var entry = NewEntry("google-drive", "abc", CreateLocalFile());

        AssetStateStore.Save(entry);

        var found = AssetStateStore.Find("google-drive", "abc");

        Assert.NotNull(found);
        Assert.Equal(entry.LocalPath, found.LocalPath);
        Assert.Equal(entry.RemoteSize, found.RemoteSize);
        Assert.Equal("folder-1", found.RemoteParentId);
    }

    [Fact]
    public void Find_KeepsConnectionsApart_ForTheSameFileId()
    {
        var google = CreateLocalFile("google.mp4");
        var dropbox = CreateLocalFile("dropbox.mp4");

        AssetStateStore.Save(NewEntry("google-drive", "same-id", google));
        AssetStateStore.Save(NewEntry("dropbox", "same-id", dropbox));

        Assert.Equal(google, AssetStateStore.Find("google-drive", "same-id")!.LocalPath);
        Assert.Equal(dropbox, AssetStateStore.Find("dropbox", "same-id")!.LocalPath);
    }

    [Fact]
    public void Find_KeepsWebDavConnectionsApart()
    {
        var first = "webdav_" + Guid.NewGuid().ToString("N");
        var second = "webdav_" + Guid.NewGuid().ToString("N");

        AssetStateStore.Save(NewEntry(first, "/dav/a.mp4", CreateLocalFile("a.mp4")));

        Assert.NotNull(AssetStateStore.Find(first, "/dav/a.mp4"));
        Assert.Null(AssetStateStore.Find(second, "/dav/a.mp4"));
    }

    [Fact]
    public void Find_DropsEntriesWhoseLocalCopyIsGone()
    {
        var path = CreateLocalFile();

        AssetStateStore.Save(NewEntry("google-drive", "abc", path));

        File.Delete(path);

        Assert.Null(AssetStateStore.Find("google-drive", "abc"));
    }

    [Fact]
    public void Save_ReplacesTheEntryForTheSameFile()
    {
        var first = CreateLocalFile("first.mp4");
        var second = CreateLocalFile("second.mp4");

        AssetStateStore.Save(NewEntry("dropbox", "abc", first));
        AssetStateStore.Save(NewEntry("dropbox", "abc", second));

        Assert.Equal(second, AssetStateStore.Find("dropbox", "abc")!.LocalPath);
        Assert.Single(AssetStateStore.FindAll("dropbox"));
    }

    [Fact]
    public void Remove_DeletesTheEntry()
    {
        AssetStateStore.Save(NewEntry("dropbox", "abc", CreateLocalFile()));
        AssetStateStore.Remove("dropbox", "abc");

        Assert.Null(AssetStateStore.Find("dropbox", "abc"));
    }

    [Fact]
    public void Remove_LeavesOtherConnectionsAlone()
    {
        AssetStateStore.Save(NewEntry("dropbox", "abc", CreateLocalFile("a.mp4")));
        AssetStateStore.Save(NewEntry("onedrive", "abc", CreateLocalFile("b.mp4")));

        AssetStateStore.Remove("dropbox", "abc");

        Assert.NotNull(AssetStateStore.Find("onedrive", "abc"));
    }

    [Fact]
    public void FindAll_ReturnsOnlyTheRequestedConnection()
    {
        AssetStateStore.Save(NewEntry("dropbox", "a", CreateLocalFile("a.mp4")));
        AssetStateStore.Save(NewEntry("dropbox", "b", CreateLocalFile("b.mp4")));
        AssetStateStore.Save(NewEntry("onedrive", "c", CreateLocalFile("c.mp4")));

        Assert.Equal(2, AssetStateStore.FindAll("dropbox").Count);
    }

    [Fact]
    public void Find_ReturnsNull_WhenNothingWasStored()
    {
        Assert.Null(AssetStateStore.Find("dropbox", "abc"));
    }

    [Fact]
    public void Store_KeepsMoreThanTwoHundredEntries()
    {
        for (var i = 0; i < 250; i++)
        {
            AssetStateStore.Save(NewEntry("dropbox", $"id-{i}", CreateLocalFile($"f{i}.mp4")));
        }

        Assert.Equal(250, AssetStateStore.FindAll("dropbox").Count);
    }
}

public class AssetStateEntryTests
{
    private static readonly DateTime Modified = new(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);

    private static AssetStateEntry Entry => new()
    {
        RemoteModifiedTime = Modified,
        RemoteSize = 1234
    };

    [Fact]
    public void Matches_WhenBothValuesAreEqual()
    {
        Assert.True(Entry.Matches(Modified, 1234));
    }

    [Fact]
    public void DoesNotMatch_WhenTheRemoteWasUpdated()
    {
        Assert.False(Entry.Matches(Modified.AddMinutes(1), 1234));
    }

    [Fact]
    public void DoesNotMatch_WhenTheSizeChanged()
    {
        Assert.False(Entry.Matches(Modified, 1235));
    }
}

public class AssetStateResolverTests
{
    private static readonly DateTime Modified = new(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);

    private static AssetStateEntry Entry(DateTime? modified = null, long? size = 100)
        => new() { RemoteModifiedTime = modified ?? Modified, RemoteSize = size };

    [Fact]
    public void NotDownloaded_WhenThereIsNoEntry()
    {
        Assert.Equal(AssetState.NotDownloaded, AssetStateResolver.Resolve(Modified, 100, null, true));
    }

    [Fact]
    public void NotDownloaded_WhenTheLocalFileIsMissing()
    {
        Assert.Equal(AssetState.NotDownloaded, AssetStateResolver.Resolve(Modified, 100, Entry(), false));
    }

    [Fact]
    public void Downloaded_WhenTheEntryMatchesTheRemote()
    {
        Assert.Equal(AssetState.Downloaded, AssetStateResolver.Resolve(Modified, 100, Entry(), true));
    }

    [Fact]
    public void Stale_WhenTheRemoteWasUpdated()
    {
        Assert.Equal(AssetState.Stale, AssetStateResolver.Resolve(Modified.AddHours(1), 100, Entry(), true));
    }

    [Fact]
    public void Stale_WhenTheRemoteSizeChanged()
    {
        Assert.Equal(AssetState.Stale, AssetStateResolver.Resolve(Modified, 200, Entry(), true));
    }

    [Fact]
    public void Downloaded_WhenNeitherSideHasMetadata()
    {
        var blank = new AssetStateEntry { RemoteModifiedTime = null, RemoteSize = null };

        Assert.Equal(AssetState.Downloaded, AssetStateResolver.Resolve(null, null, blank, true));
    }

    [Fact]
    public void Stale_WhenTheRemoteLostItsMetadata()
    {
        Assert.Equal(AssetState.Stale, AssetStateResolver.Resolve(null, null, Entry(), true));
    }
}
