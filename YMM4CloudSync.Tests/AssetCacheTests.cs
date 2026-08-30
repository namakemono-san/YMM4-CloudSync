using System.IO;
using Xunit;
using YMM4CloudSync.Core.Commons.Configuration;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Tests;

[Collection("AssetState")]
public sealed class AssetCacheTests : IDisposable
{
    private readonly string _workDir;

    public AssetCacheTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "assetcache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        AssetListingCache.PathOverride = Path.Combine(_workDir, "asset_listing.json");
        AssetRootCache.PathOverride = Path.Combine(_workDir, "asset_roots.json");
    }

    public void Dispose()
    {
        AssetListingCache.PathOverride = null;
        AssetRootCache.PathOverride = null;

        try { Directory.Delete(_workDir, true); } catch { }
    }

    private static CloudFile File(string id, string name, string mime = "image/png")
        => new(id, name, mime, 123, new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc), "parent");

    [Fact]
    public void Listing_RoundTripsEveryField()
    {
        var original = File("a", "背景.png");

        AssetListingCache.Save("onedrive", "folder-1", [original]);

        var restored = AssetListingCache.Find("onedrive", "folder-1");

        Assert.NotNull(restored);
        Assert.Equal(original, Assert.Single(restored));
    }

    [Fact]
    public void Listing_PreservesFolderDetection()
    {
        AssetListingCache.Save("onedrive", "f", [File("a", "sub", CloudMimeTypes.OneDriveFolder)]);

        Assert.True(AssetListingCache.Find("onedrive", "f")![0].IsFolder);
    }

    [Fact]
    public void Listing_PreservesOrder()
    {
        AssetListingCache.Save("dropbox", "f", [File("1", "a"), File("2", "b"), File("3", "c")]);

        Assert.Equal(["1", "2", "3"], AssetListingCache.Find("dropbox", "f")!.Select(f => f.Id));
    }

    [Fact]
    public void Listing_IsPerFolderAndPerConnection()
    {
        AssetListingCache.Save("onedrive", "shared", [File("one", "a")]);
        AssetListingCache.Save("dropbox", "shared", [File("drop", "a")]);
        AssetListingCache.Save("onedrive", "other", [File("x", "a")]);

        Assert.Equal("one", AssetListingCache.Find("onedrive", "shared")![0].Id);
        Assert.Equal("drop", AssetListingCache.Find("dropbox", "shared")![0].Id);
        Assert.Null(AssetListingCache.Find("dropbox", "other"));
    }

    [Fact]
    public void Listing_SaveReplacesThePreviousContent()
    {
        AssetListingCache.Save("onedrive", "f", [File("old", "a"), File("gone", "b")]);
        AssetListingCache.Save("onedrive", "f", [File("new", "a")]);

        Assert.Equal(["new"], AssetListingCache.Find("onedrive", "f")!.Select(f => f.Id));
    }

    [Fact]
    public void Listing_DistinguishesEmptyFromUnknown()
    {
        AssetListingCache.Save("onedrive", "empty", []);

        Assert.Empty(AssetListingCache.Find("onedrive", "empty")!);
        Assert.Null(AssetListingCache.Find("onedrive", "never-seen"));
    }

    [Fact]
    public void Listing_ForgetDropsOnlyThatFolder()
    {
        AssetListingCache.Save("onedrive", "a", [File("1", "x")]);
        AssetListingCache.Save("onedrive", "b", [File("2", "y")]);

        AssetListingCache.Forget("onedrive", "a");

        Assert.Null(AssetListingCache.Find("onedrive", "a"));
        Assert.NotNull(AssetListingCache.Find("onedrive", "b"));
    }

    [Fact]
    public void Listing_ForgetConnectionDropsEveryFolderOfIt()
    {
        AssetListingCache.Save("onedrive", "a", [File("1", "x")]);
        AssetListingCache.Save("onedrive", "b", [File("2", "y")]);
        AssetListingCache.Save("dropbox", "a", [File("3", "z")]);

        AssetListingCache.ForgetConnection("onedrive");

        Assert.Null(AssetListingCache.Find("onedrive", "a"));
        Assert.Null(AssetListingCache.Find("onedrive", "b"));
        Assert.NotNull(AssetListingCache.Find("dropbox", "a"));
    }

    [Fact]
    public void Listing_KeepsTheMostRecentFoldersOnly()
    {
        for (var i = 0; i < 55; i++)
            AssetListingCache.Save("onedrive", $"folder-{i}", [File($"{i}", "x")]);

        Assert.NotNull(AssetListingCache.Find("onedrive", "folder-54"));
        Assert.Null(AssetListingCache.Find("onedrive", "folder-0"));
    }

    [Fact]
    public void Listing_IgnoresBlankKeys()
    {
        AssetListingCache.Save("", "f", [File("1", "x")]);
        AssetListingCache.Save("onedrive", "", [File("2", "y")]);

        Assert.Null(AssetListingCache.Find("", "f"));
        Assert.Null(AssetListingCache.Find("onedrive", ""));
    }

    [Fact]
    public void Root_RoundTripsPerConnection()
    {
        AssetRootCache.Save("onedrive", "root-1");
        AssetRootCache.Save("google-drive", "root-2");

        Assert.Equal("root-1", AssetRootCache.Find("onedrive"));
        Assert.Equal("root-2", AssetRootCache.Find("google-drive"));
        Assert.Null(AssetRootCache.Find("dropbox"));
    }

    [Fact]
    public void Root_SaveOverwritesAStaleId()
    {
        AssetRootCache.Save("onedrive", "old");
        AssetRootCache.Save("onedrive", "new");

        Assert.Equal("new", AssetRootCache.Find("onedrive"));
    }

    [Fact]
    public void Root_ForgetRemovesIt()
    {
        AssetRootCache.Save("onedrive", "root-1");
        AssetRootCache.Forget("onedrive");

        Assert.Null(AssetRootCache.Find("onedrive"));
    }

    [Fact]
    public void Root_IgnoresBlankValues()
    {
        AssetRootCache.Save("onedrive", "");
        AssetRootCache.Save("", "root");

        Assert.Null(AssetRootCache.Find("onedrive"));
        Assert.Null(AssetRootCache.Find(""));
    }
}
