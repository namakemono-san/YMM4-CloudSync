using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;
using YMM4CloudSync.YMMX.Core;

namespace YMM4CloudSync.Tests;

public sealed class TachieDirectoryPackTests : IDisposable
{
    private const string TachieType =
        "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.CharacterParameter, YukkuriMovieMaker.Plugin.Tachie.AnimationTachie";

    private readonly string _workDir;
    private readonly string _tachieDir;

    public TachieDirectoryPackTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ymmx_tachie_" + Guid.NewGuid().ToString("N"));
        _tachieDir = Path.Combine(_workDir, "立ち絵素材");

        Directory.CreateDirectory(Path.Combine(_tachieDir, "口"));
        Directory.CreateDirectory(Path.Combine(_tachieDir, "目"));
        Directory.CreateDirectory(Path.Combine(_tachieDir, "体"));

        foreach (var name in new[] { "あいうえお.png", "あいうえお.a.png", "あいうえお.i.png", "あいうえお.u.png", "あいうえお.e.png", "あいうえお.o.png", "あいうえお.ini" })
            File.WriteAllText(Path.Combine(_tachieDir, "口", name), name);

        File.WriteAllText(Path.Combine(_tachieDir, "目", "デフォルト.png"), "eye");
        File.WriteAllText(Path.Combine(_tachieDir, "目", "デフォルト.0.png"), "eye-closed");
        File.WriteAllText(Path.Combine(_tachieDir, "体", "デフォルト.png"), "body");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { /* ignored */ }
    }

    private string WriteProject()
    {
        var project = new JsonObject
        {
            ["FilePath"] = Path.Combine(_workDir, "source.ymmp"),
            ["Characters"] = new JsonArray
            {
                new JsonObject
                {
                    ["TachieCharacterParameter"] = new JsonObject
                    {
                        ["$type"] = TachieType,
                        ["Directory"] = _tachieDir,
                        ["MouthSensitivity"] = 100.0
                    }
                }
            },
            ["Items"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.FaceParameter, X",
                    ["Mouth"] = Path.Combine(_tachieDir, "口", "あいうえお.png"),
                    ["Eye"] = Path.Combine(_tachieDir, "目", "デフォルト.png"),
                    ["Body"] = Path.Combine(_tachieDir, "体", "デフォルト.png"),
                    ["Hair"] = null
                }
            }
        };

        var path = Path.Combine(_workDir, "source.ymmp");
        File.WriteAllText(path, project.ToJsonString());

        return path;
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

    private string Pack()
    {
        var output = Path.Combine(_workDir, "out.ymmx");

        var result = YmmxPacker.Pack(WriteProject(), output, "tachie");

        Assert.True(result.Success);

        return output;
    }

    [Fact]
    public void PackagesTheWholeTachieDirectory()
    {
        var names = EntryNames(Pack());

        Assert.Contains("assets/tachie/立ち絵素材/口/あいうえお.png", names);
        Assert.Contains("assets/tachie/立ち絵素材/口/あいうえお.a.png", names);
        Assert.Contains("assets/tachie/立ち絵素材/口/あいうえお.o.png", names);
        Assert.Contains("assets/tachie/立ち絵素材/目/デフォルト.0.png", names);
        Assert.Contains("assets/tachie/立ち絵素材/体/デフォルト.png", names);
    }

    [Fact]
    public void PackagesPerPartIniFiles()
    {
        Assert.Contains("assets/tachie/立ち絵素材/口/あいうえお.ini", EntryNames(Pack()));
    }

    [Fact]
    public void RewritesTheDirectoryProperty()
    {
        var project = ReadProject(Pack());
        var parameter = project["Characters"]![0]!["TachieCharacterParameter"]!;

        Assert.Equal("assets/tachie/立ち絵素材", parameter["Directory"]!.GetValue<string>());
    }

    [Fact]
    public void RewritesPartPathsUnderTheDirectory()
    {
        var item = ReadProject(Pack())["Items"]![0]!;

        Assert.Equal("assets/tachie/立ち絵素材/口/あいうえお.png", item["Mouth"]!.GetValue<string>());
        Assert.Equal("assets/tachie/立ち絵素材/目/デフォルト.png", item["Eye"]!.GetValue<string>());
        Assert.Equal("assets/tachie/立ち絵素材/体/デフォルト.png", item["Body"]!.GetValue<string>());
    }

    [Fact]
    public void KeepsNonPathPropertiesUntouched()
    {
        var parameter = ReadProject(Pack())["Characters"]![0]!["TachieCharacterParameter"]!;

        Assert.Equal(100.0, parameter["MouthSensitivity"]!.GetValue<double>());
    }

    [Fact]
    public void LeavesNullPartsAlone()
    {
        Assert.Null(ReadProject(Pack())["Items"]![0]!["Hair"]);
    }

    [Fact]
    public void DoesNotDuplicateTheDirectoryForTwoCharacters()
    {
        var project = (JsonObject)JsonNode.Parse(File.ReadAllText(WriteProject()))!;

        project["Characters"]!.AsArray().Add(new JsonObject
        {
            ["TachieCharacterParameter"] = new JsonObject
            {
                ["$type"] = TachieType,
                ["Directory"] = _tachieDir
            }
        });

        var source = Path.Combine(_workDir, "two.ymmp");
        File.WriteAllText(source, project.ToJsonString());

        var output = Path.Combine(_workDir, "two.ymmx");
        Assert.True(YmmxPacker.Pack(source, output, "tachie").Success);

        var names = EntryNames(output);

        Assert.Single(names, n => n == "assets/tachie/立ち絵素材/口/あいうえお.png");
        Assert.DoesNotContain(names, n => n.StartsWith("assets/tachie/立ち絵素材_1/", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoresDirectoryOnNonTachieObjects()
    {
        var project = new JsonObject
        {
            ["FilePath"] = Path.Combine(_workDir, "plain.ymmp"),
            ["Note"] = new JsonObject
            {
                ["$type"] = "YukkuriMovieMaker.Project.Items.TextItem, X",
                ["Directory"] = _tachieDir
            }
        };

        var source = Path.Combine(_workDir, "plain.ymmp");
        File.WriteAllText(source, project.ToJsonString());

        var output = Path.Combine(_workDir, "plain.ymmx");
        Assert.True(YmmxPacker.Pack(source, output, "plain").Success);

        Assert.DoesNotContain(EntryNames(output), n => n.StartsWith("assets/tachie/", StringComparison.Ordinal));
        Assert.Equal(_tachieDir, ReadProject(output)["Note"]!["Directory"]!.GetValue<string>());
    }

    [Fact]
    public void RoundTripsBackToAbsolutePaths()
    {
        var packed = Pack();
        var destination = Path.Combine(_workDir, "extracted");

        var result = YmmxExtractor.Extract(packed, destination);

        Assert.True(result.Success);

        var project = (JsonObject)JsonNode.Parse(File.ReadAllText(result.YmmpPath))!;
        var parameter = project["Characters"]![0]!["TachieCharacterParameter"]!;
        var item = project["Items"]![0]!;

        var expectedRoot = Path.Combine(destination, "assets", "tachie", "立ち絵素材");

        Assert.Equal(expectedRoot, parameter["Directory"]!.GetValue<string>());
        Assert.Equal(Path.Combine(expectedRoot, "口", "あいうえお.png"), item["Mouth"]!.GetValue<string>());

        Assert.True(File.Exists(Path.Combine(expectedRoot, "口", "あいうえお.a.png")));
        Assert.True(File.Exists(Path.Combine(expectedRoot, "口", "あいうえお.ini")));
    }
}
