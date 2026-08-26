using Xunit;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Tests;

public class CloudFileTests
{
    private static CloudFile File(string name, string mimeType) =>
        new("id", name, mimeType, 1, DateTime.UnixEpoch);

    [Theory]
    [InlineData(CloudMimeTypes.GoogleFolder)]
    [InlineData(CloudMimeTypes.OneDriveFolder)]
    [InlineData(CloudMimeTypes.DropboxFolder)]
    public void IsFolder_IsTrue_ForEveryProviderFolderMimeType(string mimeType)
    {
        Assert.True(File("project.ymmx", mimeType).IsFolder);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/zip")]
    [InlineData("")]
    public void IsFolder_IsFalse_ForFileMimeTypes(string mimeType)
    {
        Assert.False(File("project.ymmx", mimeType).IsFolder);
    }

    [Fact]
    public void IsFolder_IsFalse_WhenMimeTypeIsUnknown()
    {
        Assert.False(CloudMimeTypes.IsFolder(null));
    }

    [Fact]
    public void FolderNamedLikeAProject_IsExcludedByTheListFilter()
    {
        var entries = new[]
        {
            File("real.ymmx", "application/octet-stream"),
            File("decoy.ymmx", CloudMimeTypes.DropboxFolder),
            File("notes.txt", "text/plain")
        };

        var listed = entries
            .Where(f => !f.IsFolder && f.Name.EndsWith(".ymmx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(listed);
        Assert.Equal("real.ymmx", listed[0].Name);
    }
}
