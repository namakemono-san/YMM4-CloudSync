using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using YMM4CloudSync.YMMX.Core;

namespace YMM4CloudSync.Tests;

public sealed class YmmxExtractorTests : IDisposable
{
    private readonly string _workDir;

    public YmmxExtractorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ymmx_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { /* ignored */ }
    }

    private const string MetaJson = """
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "name": "test",
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-01-01T00:00:00Z",
          "format_version": 1,
          "plugin_version": "0.3.0",
          "min_plugin_version": "0.1.0"
        }
        """;

    private string BuildYmmx(string ymmpJson, Action<ZipArchive>? extraEntries = null)
    {
        var path = Path.Combine(_workDir, Guid.NewGuid().ToString("N") + ".ymmx");

        using (var stream = new FileStream(path, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "meta.json", MetaJson);
            WriteEntry(archive, "project.ymmp", ymmpJson);
            WriteEntry(archive, "assets/images/pic.png", "not really a png");
            extraEntries?.Invoke(archive);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private string OutputDir() => Path.Combine(_workDir, "out_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Extract_RewritesPackedAssetPathsToAbsolute()
    {
        var ymmp = """
            {
              "FilePath": "C:\\Users\\author\\project.ymmp",
              "Items": [ { "FilePath": "assets/images/pic.png" } ]
            }
            """;

        var ymmxPath = BuildYmmx(ymmp);
        var outputDir = OutputDir();

        var result = YmmxExtractor.Extract(ymmxPath, outputDir);

        Assert.True(result.Success);

        var written = JsonNode.Parse(File.ReadAllText(result.YmmpPath))!;
        var rewritten = written["Items"]![0]!["FilePath"]!.GetValue<string>();

        Assert.Equal(Path.Combine(outputDir, "assets", "images", "pic.png"), rewritten);
    }

    [Fact]
    public void Extract_LeavesRootFilePathAlone()
    {
        const string authorPath = @"C:\Users\author\project.ymmp";

        var ymmp = JsonSerializer.Serialize(new
        {
            FilePath = authorPath,
            Items = new[] { new { FilePath = "assets/images/pic.png" } }
        });

        var ymmxPath = BuildYmmx(ymmp);

        var result = YmmxExtractor.Extract(ymmxPath, OutputDir());

        var written = JsonNode.Parse(File.ReadAllText(result.YmmpPath))!;

        Assert.Equal(authorPath, written["FilePath"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"\\server\share\secret.txt")]
    [InlineData("assets/../../../etc/passwd")]
    [InlineData(@"assets\..\..\outside.png")]
    [InlineData("../outside.png")]
    public void Extract_NeutralizesFilePathsOutsideThePackage(string hostilePath)
    {
        var ymmp = JsonSerializer.Serialize(new
        {
            Items = new[] { new { FilePath = hostilePath } }
        });

        var ymmxPath = BuildYmmx(ymmp);
        var outputDir = OutputDir();

        var result = YmmxExtractor.Extract(ymmxPath, outputDir);

        Assert.True(result.Success);
        Assert.Equal([hostilePath], result.ExternalReferences);

        var written = JsonNode.Parse(File.ReadAllText(result.YmmpPath))!;
        var rewritten = written["Items"]![0]!["FilePath"]!.GetValue<string>();

        Assert.StartsWith(
            Path.GetFullPath(outputDir) + Path.DirectorySeparatorChar,
            Path.GetFullPath(rewritten),
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(rewritten));
    }

    [Fact]
    public void Extract_ReportsNoExternalReferences_ForACleanPackage()
    {
        var ymmp = JsonSerializer.Serialize(new
        {
            Items = new[] { new { FilePath = "assets/images/pic.png" } }
        });

        var result = YmmxExtractor.Extract(BuildYmmx(ymmp), OutputDir());

        Assert.Empty(result.ExternalReferences);
    }

    [Fact]
    public void Extract_DeduplicatesExternalReferences()
    {
        var ymmp = JsonSerializer.Serialize(new
        {
            Items = new[]
            {
                new { FilePath = @"C:\Windows\evil.png" },
                new { FilePath = @"C:\Windows\evil.png" }
            }
        });

        var result = YmmxExtractor.Extract(BuildYmmx(ymmp), OutputDir());

        Assert.Single(result.ExternalReferences);
    }

    [Fact]
    public void Extract_RemovesOutputDirectory_WhenExtractionFails()
    {
        var ymmp = JsonSerializer.Serialize(new { Items = Array.Empty<object>() });

        var ymmxPath = BuildYmmx(ymmp, archive => WriteEntry(archive, "../escaped.txt", "nope"));
        var outputDir = OutputDir();

        Assert.Throws<InvalidDataException>(() => YmmxExtractor.Extract(ymmxPath, outputDir));
        Assert.False(Directory.Exists(outputDir));
    }

    [Fact]
    public void Extract_RestoresPreviousProject_WhenExtractionFails()
    {
        var outputDir = OutputDir();
        Directory.CreateDirectory(outputDir);
        var marker = Path.Combine(outputDir, "existing.txt");
        File.WriteAllText(marker, "keep me");

        var ymmp = JsonSerializer.Serialize(new { Items = Array.Empty<object>() });

        var ymmxPath = BuildYmmx(ymmp, archive => WriteEntry(archive, "../escaped.txt", "nope"));

        Assert.Throws<InvalidDataException>(() => YmmxExtractor.Extract(ymmxPath, outputDir));

        Assert.True(File.Exists(marker));
        Assert.Equal("keep me", File.ReadAllText(marker));
    }

    [Fact]
    public void Extract_RejectsEntriesPointingOutsideTheOutputDirectory()
    {
        var ymmp = JsonSerializer.Serialize(new { Items = Array.Empty<object>() });

        var ymmxPath = BuildYmmx(ymmp, archive => WriteEntry(archive, "../escaped.txt", "nope"));

        Assert.Throws<InvalidDataException>(() => YmmxExtractor.Extract(ymmxPath, OutputDir()));
    }

    [Fact]
    public void Extract_ThrowsWhenCancelled()
    {
        var ymmp = JsonSerializer.Serialize(new { Items = Array.Empty<object>() });
        var ymmxPath = BuildYmmx(ymmp);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => YmmxExtractor.Extract(ymmxPath, OutputDir(), null, cts.Token));
    }
}
