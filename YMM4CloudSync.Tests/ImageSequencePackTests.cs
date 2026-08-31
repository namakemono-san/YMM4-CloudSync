using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Tests;

public sealed class ImageSequencePackTests : IDisposable
{
    private const string VideoItem = "YukkuriMovieMaker.Project.Items.VideoItem, YukkuriMovieMaker";
    private const string ImageItem = "YukkuriMovieMaker.Project.Items.ImageItem, YukkuriMovieMaker";

    private readonly string _workDir;
    private readonly string _framesDir;

    public ImageSequencePackTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ymmx_seq_" + Guid.NewGuid().ToString("N"));
        _framesDir = Path.Combine(_workDir, "frames");

        Directory.CreateDirectory(_framesDir);

        for (var i = 1; i <= 5; i++)
            File.WriteAllText(Path.Combine(_framesDir, $"image_{i:000}.png"), $"frame{i}");

        File.WriteAllText(Path.Combine(_framesDir, "video.mp4"), "unrelated");
        File.WriteAllText(Path.Combine(_framesDir, "note.txt"), "unrelated");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { /* ignored */ }
    }

    private string Head => Path.Combine(_framesDir, "image_001.png");

    private string WriteProject(string type, string filePath)
    {
        var project = new JsonObject
        {
            ["FilePath"] = Path.Combine(_workDir, "source.ymmp"),
            ["Items"] = new JsonArray
            {
                new JsonObject { ["$type"] = type, ["FilePath"] = filePath }
            }
        };

        var path = Path.Combine(_workDir, "source.ymmp");
        File.WriteAllText(path, project.ToJsonString());

        return path;
    }

    private string Pack(string type, string filePath)
    {
        var output = Path.Combine(_workDir, "out.ymmx");

        Assert.True(YmmxPacker.Pack(WriteProject(type, filePath), output, "seq").Success);

        return output;
    }

    private static List<string> EntryNames(string ymmxPath)
    {
        using var archive = ZipFile.OpenRead(ymmxPath);

        return [.. archive.Entries.Select(e => e.FullName)];
    }

    private static JsonObject ReadProject(string ymmxPath)
    {
        using var archive = ZipFile.OpenRead(ymmxPath);
        using var stream = archive.GetEntry("project.ymmp")!.Open();
        using var reader = new StreamReader(stream);

        return (JsonObject)JsonNode.Parse(reader.ReadToEnd())!;
    }

    [Fact]
    public void FindsEveryContiguousFrame()
    {
        var frames = ImageSequence.TryGetFrames(Head);

        Assert.NotNull(frames);
        Assert.Equal(5, frames.Count);
    }

    [Fact]
    public void StopsAtAGapInTheNumbering()
    {
        File.Delete(Path.Combine(_framesDir, "image_003.png"));

        Assert.Equal(2, ImageSequence.TryGetFrames(Head)!.Count);
    }

    [Fact]
    public void IgnoresASingleImageWithNoSiblings()
    {
        var lone = Path.Combine(_workDir, "alone_001.png");
        File.WriteAllText(lone, "x");

        Assert.Null(ImageSequence.TryGetFrames(lone));
    }

    [Fact]
    public void IgnoresNamesWithoutATrailingNumber()
    {
        var plain = Path.Combine(_framesDir, "picture.png");
        File.WriteAllText(plain, "x");

        Assert.Null(ImageSequence.TryGetFrames(plain));
    }

    [Fact]
    public void PacksEveryFrame()
    {
        var names = EntryNames(Pack(VideoItem, Head));

        for (var i = 1; i <= 5; i++)
            Assert.Contains($"assets/sequence/image_001/image_{i:000}.png", names);
    }

    [Fact]
    public void DoesNotPackUnrelatedNeighbours()
    {
        var names = EntryNames(Pack(VideoItem, Head));

        Assert.DoesNotContain(names, n => n.EndsWith("video.mp4", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.EndsWith("note.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void PointsTheProjectAtTheHeadFrame()
    {
        var item = ReadProject(Pack(VideoItem, Head))["Items"]![0]!;

        Assert.Equal("assets/sequence/image_001/image_001.png", item["FilePath"]!.GetValue<string>());
    }

    [Fact]
    public void TreatsAnImageItemAsASingleFile()
    {
        var packed = Pack(ImageItem, Head);
        var names = EntryNames(packed);

        Assert.Contains("assets/images/image_001.png", names);
        Assert.DoesNotContain(names, n => n.StartsWith("assets/sequence/", StringComparison.Ordinal));
    }

    [Fact]
    public void TreatsANonImageVideoAsASingleFile()
    {
        var video = Path.Combine(_framesDir, "clip_001.mp4");
        File.WriteAllText(video, "x");
        File.WriteAllText(Path.Combine(_framesDir, "clip_002.mp4"), "x");

        var names = EntryNames(Pack(VideoItem, video));

        Assert.Contains("assets/videos/clip_001.mp4", names);
        Assert.DoesNotContain(names, n => n.StartsWith("assets/sequence/", StringComparison.Ordinal));
    }

    [Fact]
    public void RoundTripsWithEveryFrameOnDisk()
    {
        var destination = Path.Combine(_workDir, "extracted");

        var result = YmmxExtractor.Extract(Pack(VideoItem, Head), destination);

        Assert.True(result.Success);

        var item = ((JsonObject)JsonNode.Parse(File.ReadAllText(result.YmmpPath))!)["Items"]![0]!;
        var head = item["FilePath"]!.GetValue<string>();

        Assert.True(File.Exists(head));

        var folder = Path.GetDirectoryName(head)!;

        for (var i = 1; i <= 5; i++)
            Assert.True(File.Exists(Path.Combine(folder, $"image_{i:000}.png")));
    }
}
