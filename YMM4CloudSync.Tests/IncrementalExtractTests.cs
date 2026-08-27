using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Tests;

public sealed class IncrementalExtractTests : IDisposable
{
    private readonly string _workDir;

    public IncrementalExtractTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ymmx_incr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { /* ignored */ }
    }

    private string WriteAsset(string name, int size, byte seed)
    {
        var path = Path.Combine(_workDir, name);
        var bytes = new byte[size];

        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i + seed) % 251);

        File.WriteAllBytes(path, bytes);

        return path;
    }

    private string WriteProject(string name, params string[] assetPaths)
    {
        var items = new JsonArray();

        foreach (var assetPath in assetPaths) items.Add(new JsonObject { ["FilePath"] = assetPath });

        var project = new JsonObject
        {
            ["FilePath"] = Path.Combine(_workDir, name),
            ["Items"] = items
        };

        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, project.ToJsonString());

        return path;
    }

    private string Pack(string ymmpPath, string archiveName)
    {
        var ymmxPath = Path.Combine(_workDir, archiveName);

        var result = YmmxPacker.Pack(ymmpPath, ymmxPath, Path.GetFileNameWithoutExtension(archiveName));

        Assert.True(result.Success);

        return ymmxPath;
    }

    [Fact]
    public void SecondExtract_KeepsUnchangedAssetsUntouched()
    {
        var stableAsset = WriteAsset("stable.mp4", 400_000, 1);
        var outputDir = Path.Combine(_workDir, "out");

        var first = Pack(WriteProject("a.ymmp", stableAsset), "first.ymmx");
        var firstResult = YmmxExtractor.Extract(first, outputDir);

        Assert.True(firstResult.Success);

        var extractedAsset = Path.Combine(outputDir, "assets", "other", "stable.mp4");
        var writtenAt = File.GetLastWriteTimeUtc(extractedAsset);

        Thread.Sleep(50);

        var second = Pack(WriteProject("b.ymmp", stableAsset), "second.ymmx");
        var secondResult = YmmxExtractor.Extract(second, outputDir, (_, _) => ExtractConflictAction.Overwrite);

        Assert.True(secondResult.Success);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(extractedAsset));
    }

    [Fact]
    public void SecondExtract_ReplacesChangedAssets()
    {
        var asset = WriteAsset("clip.mp4", 200_000, 1);
        var outputDir = Path.Combine(_workDir, "out");

        YmmxExtractor.Extract(Pack(WriteProject("a.ymmp", asset), "first.ymmx"), outputDir);

        File.WriteAllBytes(asset, new byte[200_000]);

        var result = YmmxExtractor.Extract(
            Pack(WriteProject("b.ymmp", asset), "second.ymmx"),
            outputDir,
            (_, _) => ExtractConflictAction.Overwrite);

        Assert.True(result.Success);

        var extracted = Path.Combine(outputDir, "assets", "other", "clip.mp4");

        Assert.Equal(new byte[200_000], File.ReadAllBytes(extracted));
    }

    [Fact]
    public void SecondExtract_RemovesAssetsMissingFromTheNewPackage()
    {
        var kept = WriteAsset("kept.mp4", 50_000, 1);
        var dropped = WriteAsset("dropped.mp4", 50_000, 2);
        var outputDir = Path.Combine(_workDir, "out");

        YmmxExtractor.Extract(Pack(WriteProject("a.ymmp", kept, dropped), "first.ymmx"), outputDir);

        var droppedPath = Path.Combine(outputDir, "assets", "other", "dropped.mp4");
        Assert.True(File.Exists(droppedPath));

        YmmxExtractor.Extract(
            Pack(WriteProject("b.ymmp", kept), "second.ymmx"),
            outputDir,
            (_, _) => ExtractConflictAction.Overwrite);

        Assert.False(File.Exists(droppedPath));
        Assert.True(File.Exists(Path.Combine(outputDir, "assets", "other", "kept.mp4")));
    }

    [Fact]
    public void SecondExtract_LeavesACompleteBackup()
    {
        var asset = WriteAsset("clip.mp4", 100_000, 1);
        var outputDir = Path.Combine(_workDir, "out");

        YmmxExtractor.Extract(Pack(WriteProject("a.ymmp", asset), "first.ymmx"), outputDir);

        var original = File.ReadAllBytes(Path.Combine(outputDir, "assets", "other", "clip.mp4"));

        File.WriteAllBytes(asset, new byte[100_000]);

        var result = YmmxExtractor.Extract(
            Pack(WriteProject("b.ymmp", asset), "second.ymmx"),
            outputDir,
            (_, _) => ExtractConflictAction.Overwrite);

        Assert.NotNull(result.BackupDirectory);

        var backedUp = Path.Combine(result.BackupDirectory, "assets", "other", "clip.mp4");

        Assert.True(File.Exists(backedUp));
        Assert.Equal(original, File.ReadAllBytes(backedUp));
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory, "meta.json")));
    }

    [Fact]
    public void SecondExtract_ProducesTheSameContentAsAFreshExtract()
    {
        var a = WriteAsset("a.mp4", 120_000, 1);
        var b = WriteAsset("b.mp4", 90_000, 2);

        var incrementalDir = Path.Combine(_workDir, "incremental");
        var freshDir = Path.Combine(_workDir, "fresh");

        YmmxExtractor.Extract(Pack(WriteProject("first.ymmp", a), "first.ymmx"), incrementalDir);

        var second = Pack(WriteProject("second.ymmp", a, b), "second.ymmx");

        YmmxExtractor.Extract(second, incrementalDir, (_, _) => ExtractConflictAction.Overwrite);
        YmmxExtractor.Extract(second, freshDir);

        var incremental = Snapshot(incrementalDir);
        var fresh = Snapshot(freshDir);

        Assert.Equal(fresh.Keys.OrderBy(k => k), incremental.Keys.OrderBy(k => k));

        foreach (var (name, hash) in fresh)
        {
            Assert.Equal(hash, incremental[name]);
        }
    }

    [Fact]
    public void SecondExtract_SucceedsWhileAnUnchangedAssetIsOpen()
    {
        var stable = WriteAsset("stable.mp4", 120_000, 1);
        var changing = WriteAsset("changing.mp4", 80_000, 2);
        var outputDir = Path.Combine(_workDir, "out");

        YmmxExtractor.Extract(Pack(WriteProject("a.ymmp", stable, changing), "first.ymmx"), outputDir);

        var stableExtracted = Path.Combine(outputDir, "assets", "other", "stable.mp4");

        File.WriteAllBytes(changing, new byte[80_000]);

        var second = Pack(WriteProject("b.ymmp", stable, changing), "second.ymmx");

        using (new FileStream(stableExtracted, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = YmmxExtractor.Extract(second, outputDir, (_, _) => ExtractConflictAction.Overwrite);

            Assert.True(result.Success);
        }

        Assert.Equal(new byte[80_000], File.ReadAllBytes(Path.Combine(outputDir, "assets", "other", "changing.mp4")));
    }

    [Fact]
    public void SecondExtract_ReportsAClearError_WhenAChangedAssetIsOpen()
    {
        var asset = WriteAsset("clip.mp4", 80_000, 1);
        var outputDir = Path.Combine(_workDir, "out");

        YmmxExtractor.Extract(Pack(WriteProject("a.ymmp", asset), "first.ymmx"), outputDir);

        var extracted = Path.Combine(outputDir, "assets", "other", "clip.mp4");
        var original = File.ReadAllBytes(extracted);

        File.WriteAllBytes(asset, new byte[80_000]);

        var second = Pack(WriteProject("b.ymmp", asset), "second.ymmx");

        using (new FileStream(extracted, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.ThrowsAny<Exception>(
                () => YmmxExtractor.Extract(second, outputDir, (_, _) => ExtractConflictAction.Overwrite));

            Assert.Contains("YMM4 で開いたまま", error.Message + error.InnerException?.Message);
        }

        Assert.Equal(original, File.ReadAllBytes(extracted));
    }

    private static Dictionary<string, uint> Snapshot(string directory)
    {
        var buffer = new byte[64 * 1024];

        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("meta.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                f => Path.GetRelativePath(directory, f).Replace('\\', '/'),
                f => Crc32.ComputeFile(f, buffer));
    }
}

public class Crc32Tests
{
    [Fact]
    public void MatchesTheCrcStoredInAZipEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "crc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var payload = Enumerable.Range(0, 50_000).Select(i => (byte)(i % 253)).ToArray();
            var source = Path.Combine(directory, "data.bin");
            File.WriteAllBytes(source, payload);

            var zipPath = Path.Combine(directory, "a.zip");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(source, "data.bin", CompressionLevel.NoCompression);
            }

            using var archive = ZipFile.OpenRead(zipPath);

            Assert.Equal(archive.Entries[0].Crc32, Crc32.ComputeFile(source, new byte[4096]));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void ReturnsZeroForEmptyInput()
    {
        using var stream = new MemoryStream();

        Assert.Equal(0u, Crc32.Compute(stream, new byte[64]));
    }
}
