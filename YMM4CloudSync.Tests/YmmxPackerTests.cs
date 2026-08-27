using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Tests;

public sealed class YmmxPackerTests : IDisposable
{
    private readonly string _workDir;

    public YmmxPackerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ymmx_pack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { /* ignored */ }
    }

    private string WriteAsset(string name, int sizeBytes)
    {
        var path = Path.Combine(_workDir, name);
        var bytes = new byte[sizeBytes];

        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);

        File.WriteAllBytes(path, bytes);

        return path;
    }

    private string WriteProject(params string[] assetPaths)
        => WriteProject(assetPaths.Select(p => (p, (string?)null)).ToArray());

    private string WriteProject(params (string AssetPath, string? Type)[] assets)
    {
        var items = new JsonArray();

        foreach (var (assetPath, type) in assets)
        {
            var item = new JsonObject { ["FilePath"] = assetPath };

            if (type != null) item["$type"] = type;

            items.Add(item);
        }

        var project = new JsonObject
        {
            ["FilePath"] = Path.Combine(_workDir, "source.ymmp"),
            ["Items"] = items
        };

        var path = Path.Combine(_workDir, "source.ymmp");
        File.WriteAllText(path, project.ToJsonString());

        return path;
    }

    [Fact]
    public void PackedArchive_ExtractsWithoutHashMismatch()
    {
        var ymmpPath = WriteProject(
            WriteAsset("clip.mp4", 300_000),
            WriteAsset("art.psd", 120_000),
            WriteAsset("voice.wav", 90_000));

        var ymmxPath = Path.Combine(_workDir, "out.ymmx");

        var packResult = YmmxPacker.Pack(ymmpPath, ymmxPath, "test");

        Assert.True(packResult.Success);
        Assert.Empty(packResult.MissingFiles);

        var extractResult = YmmxExtractor.Extract(ymmxPath, Path.Combine(_workDir, "extracted"));

        Assert.True(extractResult.Success);
        Assert.False(extractResult.HashMismatch);
        Assert.Empty(extractResult.ExternalReferences);
    }

    [Fact]
    public void PackedArchive_StoresMetaJsonLast()
    {
        var ymmpPath = WriteProject(WriteAsset("clip.mp4", 50_000));
        var ymmxPath = Path.Combine(_workDir, "out.ymmx");

        YmmxPacker.Pack(ymmpPath, ymmxPath, "test");

        using var archive = ZipFile.OpenRead(ymmxPath);

        Assert.Equal("meta.json", archive.Entries[^1].FullName);
    }

    [Fact]
    public void PackedArchive_StoresAlreadyCompressedAssetsWithoutDeflating()
    {
        var ymmpPath = WriteProject(WriteAsset("clip.mp4", 200_000), WriteAsset("notes.txt", 200_000));
        var ymmxPath = Path.Combine(_workDir, "out.ymmx");

        YmmxPacker.Pack(ymmpPath, ymmxPath, "test");

        using var archive = ZipFile.OpenRead(ymmxPath);

        var video = archive.Entries.Single(e => e.FullName.EndsWith("clip.mp4", StringComparison.Ordinal));
        var text = archive.Entries.Single(e => e.FullName.EndsWith("notes.txt", StringComparison.Ordinal));

        Assert.Equal(video.Length, video.CompressedLength);
        Assert.True(text.CompressedLength < text.Length);
    }

    [Fact]
    public void Pack_PacksASharedAssetOnce_RegardlessOfPathCasing()
    {
        var asset = WriteAsset("shared.png", 40_000);

        var ymmpPath = WriteProject(asset, asset.ToUpperInvariant());
        var ymmxPath = Path.Combine(_workDir, "out.ymmx");

        YmmxPacker.Pack(ymmpPath, ymmxPath, "test");

        using var archive = ZipFile.OpenRead(ymmxPath);

        Assert.Single(archive.Entries, e => e.FullName.Contains("shared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_ReportsMissingAssets()
    {
        var ymmpPath = WriteProject(
            WriteAsset("clip.mp4", 1000),
            Path.Combine(_workDir, "gone.png"));

        var result = YmmxPacker.Pack(ymmpPath, Path.Combine(_workDir, "out.ymmx"), "test");

        Assert.True(result.Success);
        Assert.Single(result.MissingFiles);
    }

    [Fact]
    public void Pack_RewritesAssetPathsToRelativeForm()
    {
        var ymmpPath = WriteProject(
            (WriteAsset("clip.mp4", 1000), "YukkuriMovieMaker.Project.Items.VideoItem, YukkuriMovieMaker"));
        var ymmxPath = Path.Combine(_workDir, "out.ymmx");

        YmmxPacker.Pack(ymmpPath, ymmxPath, "test");

        using var archive = ZipFile.OpenRead(ymmxPath);
        using var reader = new StreamReader(archive.GetEntry("project.ymmp")!.Open(), Encoding.UTF8);

        var written = JsonNode.Parse(reader.ReadToEnd())!;

        Assert.Equal("assets/videos/clip.mp4", written["Items"]![0]!["FilePath"]!.GetValue<string>());
    }

    [Fact]
    public void Pack_DeletesTheOutputWhenCancelled()
    {
        var ymmpPath = WriteProject(WriteAsset("clip.mp4", 500_000));
        var ymmxPath = Path.Combine(_workDir, "out.ymmx");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => YmmxPacker.Pack(ymmpPath, ymmxPath, "test", null, cts.Token));

        Assert.False(File.Exists(ymmxPath));
    }

    [Fact]
    public void Pack_ReportsProgressEndingAtCompletion()
    {
        var ymmpPath = WriteProject(WriteAsset("clip.mp4", 400_000));

        var values = new List<double>();
        var progress = new Progress<double>(values.Add);

        YmmxPacker.Pack(ymmpPath, Path.Combine(_workDir, "out.ymmx"), "test", progress);

        Assert.NotEmpty(values);
        Assert.Equal(100.0, values[^1]);
        Assert.All(values, v => Assert.InRange(v, 0.0, 100.0));
    }
}

public class ZipCompressionPolicyTests
{
    [Theory]
    [InlineData("assets/videos/a.mp4")]
    [InlineData("assets/audio/a.MP3")]
    [InlineData("assets/images/a.png")]
    [InlineData("assets/other/a.webp")]
    public void ForEntry_SkipsCompressionForAlreadyCompressedFormats(string path)
    {
        Assert.Equal(CompressionLevel.NoCompression, ZipCompressionPolicy.ForEntry(path));
    }

    [Theory]
    [InlineData("assets/other/a.psd")]
    [InlineData("assets/other/a.PSB")]
    [InlineData("assets/audio/a.wav")]
    [InlineData("assets/images/a.bmp")]
    public void ForEntry_UsesFastestForLightlyCompressibleFormats(string path)
    {
        Assert.Equal(CompressionLevel.Fastest, ZipCompressionPolicy.ForEntry(path));
    }

    [Theory]
    [InlineData("project.ymmp")]
    [InlineData("meta.json")]
    [InlineData("assets/other/readme.txt")]
    [InlineData("assets/other/noextension")]
    public void ForEntry_UsesOptimalForCompressibleFormats(string path)
    {
        Assert.Equal(CompressionLevel.Optimal, ZipCompressionPolicy.ForEntry(path));
    }
}
