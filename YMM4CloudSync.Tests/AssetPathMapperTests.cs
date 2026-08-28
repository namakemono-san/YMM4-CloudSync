using System.IO;
using Xunit;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Tests;

public class AssetPathMapperTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ymm4cs_assets");

    [Fact]
    public void GetLocalPath_PutsTheFileUnderConnectionAndFolders()
    {
        var path = AssetPathMapper.GetLocalPath(Root, "google-drive", ["背景", "森"], "朝.png");

        Assert.Equal(Path.Combine(Root, "google-drive", "背景", "森", "朝.png"), path);
    }

    [Fact]
    public void GetLocalPath_WorksAtTheRootOfTheAssetFolder()
    {
        var path = AssetPathMapper.GetLocalPath(Root, "dropbox", [], "bgm.wav");

        Assert.Equal(Path.Combine(Root, "dropbox", "bgm.wav"), path);
    }

    [Fact]
    public void GetLocalPath_SeparatesConnections()
    {
        var google = AssetPathMapper.GetLocalPath(Root, "google-drive", [], "a.png");
        var dropbox = AssetPathMapper.GetLocalPath(Root, "dropbox", [], "a.png");

        Assert.NotEqual(google, dropbox);
    }

    [Fact]
    public void GetLocalPath_AcceptsAWebDavConnectionKeyAsADirectoryName()
    {
        var key = "webdav_" + Guid.NewGuid().ToString("N");

        var path = AssetPathMapper.GetLocalPath(Root, key, [], "a.png");

        Assert.Equal(Path.Combine(Root, key, "a.png"), path);
    }

    [Fact]
    public void GetLocalPath_NeutralisesTraversalInAFolderName()
    {
        var path = AssetPathMapper.GetLocalPath(Root, "dropbox", ["..", ".."], "a.png");

        Assert.Equal(
            Path.Combine(Root, "dropbox", AssetPathMapper.FallbackName, AssetPathMapper.FallbackName, "a.png"),
            path);
    }

    [Fact]
    public void GetLocalPath_StaysUnderTheAssetDirectory()
    {
        var path = AssetPathMapper.GetLocalPath(Root, "dropbox", [@"..\..\..\windows", "system32"], "a.png");

        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetLocalPath_FlattensASeparatorInsideAName()
    {
        var path = AssetPathMapper.GetLocalPath(Root, "dropbox", ["a/b"], "c.png");

        Assert.Equal(Path.Combine(Root, "dropbox", "b", "c.png"), path);
    }

    [Fact]
    public void GetLocalFolder_StopsAtTheFolder()
    {
        var folder = AssetPathMapper.GetLocalFolder(Root, "onedrive", ["素材", "効果音"]);

        Assert.Equal(Path.Combine(Root, "onedrive", "素材", "効果音"), folder);
    }

    [Fact]
    public void GetLocalFolder_IsTheParentOfGetLocalPath()
    {
        var folder = AssetPathMapper.GetLocalFolder(Root, "onedrive", ["素材"]);
        var file = AssetPathMapper.GetLocalPath(Root, "onedrive", ["素材"], "a.png");

        Assert.Equal(folder, Path.GetDirectoryName(file));
    }
}
