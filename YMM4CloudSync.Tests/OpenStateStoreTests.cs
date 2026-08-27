using System.IO;
using Xunit;
using YMM4CloudSync.Core.Commons.Configuration;

namespace YMM4CloudSync.Tests;

[Collection("OpenState")]
public sealed class OpenStateStoreTests : IDisposable
{
    private readonly string _workDir;

    public OpenStateStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "openstate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        OpenStateStore.PathOverride = Path.Combine(_workDir, "open_state.json");
    }

    public void Dispose()
    {
        OpenStateStore.PathOverride = null;

        try { Directory.Delete(_workDir, true); } catch { /* ignored */ }
    }

    private string CreateProjectFile(string name = "project.ymmp")
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, "{}");

        return path;
    }

    private OpenStateEntry NewEntry(string ymmpPath, DateTime? modified = null, long? size = 100)
        => new()
        {
            ServiceName = "Dropbox",
            FileId = "/YMM4CloudSync/a.ymmx",
            RemoteModifiedTime = modified ?? new DateTime(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc),
            RemoteSize = size,
            YmmpPath = ymmpPath
        };

    [Fact]
    public void SaveThenFind_ReturnsTheEntry()
    {
        var entry = NewEntry(CreateProjectFile());

        OpenStateStore.Save(entry);

        var found = OpenStateStore.Find("Dropbox", "/YMM4CloudSync/a.ymmx");

        Assert.NotNull(found);
        Assert.Equal(entry.YmmpPath, found.YmmpPath);
        Assert.Equal(entry.RemoteSize, found.RemoteSize);
    }

    [Fact]
    public void Find_ReturnsNull_ForAnotherService()
    {
        OpenStateStore.Save(NewEntry(CreateProjectFile()));

        Assert.Null(OpenStateStore.Find("Google Drive", "/YMM4CloudSync/a.ymmx"));
    }

    [Fact]
    public void Find_DropsEntriesWhoseProjectIsGone()
    {
        var ymmpPath = CreateProjectFile();

        OpenStateStore.Save(NewEntry(ymmpPath));

        File.Delete(ymmpPath);

        Assert.Null(OpenStateStore.Find("Dropbox", "/YMM4CloudSync/a.ymmx"));
    }

    [Fact]
    public void Save_ReplacesTheEntryForTheSameFile()
    {
        var first = CreateProjectFile("first.ymmp");
        var second = CreateProjectFile("second.ymmp");

        OpenStateStore.Save(NewEntry(first));
        OpenStateStore.Save(NewEntry(second));

        Assert.Equal(second, OpenStateStore.Find("Dropbox", "/YMM4CloudSync/a.ymmx")!.YmmpPath);
    }

    [Fact]
    public void Remove_DeletesTheEntry()
    {
        OpenStateStore.Save(NewEntry(CreateProjectFile()));
        OpenStateStore.Remove("Dropbox", "/YMM4CloudSync/a.ymmx");

        Assert.Null(OpenStateStore.Find("Dropbox", "/YMM4CloudSync/a.ymmx"));
    }

    [Fact]
    public void Find_ReturnsNull_WhenNothingWasStored()
    {
        Assert.Null(OpenStateStore.Find("Dropbox", "/YMM4CloudSync/a.ymmx"));
    }
}

public class OpenStateEntryTests
{
    private static readonly DateTime Modified = new(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);

    private static OpenStateEntry Entry => new()
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

    [Fact]
    public void DoesNotMatch_WhenTheRemoteHasNoMetadata()
    {
        Assert.False(Entry.Matches(null, null));
    }

    [Fact]
    public void Matches_WhenBothSidesLackMetadata()
    {
        var entry = new OpenStateEntry { RemoteModifiedTime = null, RemoteSize = null };

        Assert.True(entry.Matches(null, null));
    }
}
