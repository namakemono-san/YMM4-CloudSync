using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;
using YMM4CloudSync.YMMX.Core;

namespace YMM4CloudSync.Tests;

public sealed class PsdShapeGuardPackTests : IDisposable
{
    private const string PsdShapeType =
        "YukkuriMovieMaker.Plugin.Tachie.Psd.PsdShapeParameter, YukkuriMovieMaker.Plugin.Tachie.Psd";

    private readonly string _workDir;
    private readonly string _psdPath;

    public PsdShapeGuardPackTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ymmx_psdguard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        _psdPath = Path.Combine(_workDir, "character.psd");
        File.WriteAllText(_psdPath, "psd");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { /* ignored */ }
    }

    private JsonObject BuildShapeParameter(string? guardPath)
    {
        var parameter = new JsonObject
        {
            ["$type"] = PsdShapeType,
            ["FilePath"] = _psdPath,
            ["EnableLayers"] = new JsonArray("i988", "i926"),
            ["EnableLayerPaths"] = new JsonArray("\u001c東北きりたん\u001d0")
        };

        if (guardPath != null) parameter["EnableLayersFilePath"] = guardPath;

        return parameter;
    }

    private string WriteProject(JsonObject shapeParameter)
    {
        var project = new JsonObject
        {
            ["FilePath"] = Path.Combine(_workDir, "source.ymmp"),
            ["Items"] = new JsonArray
            {
                new JsonObject { ["ShapeParameter"] = shapeParameter }
            }
        };

        var path = Path.Combine(_workDir, "source.ymmp");
        File.WriteAllText(path, project.ToJsonString());

        return path;
    }

    private (JsonObject Project, string YmmxPath) Pack(JsonObject shapeParameter)
    {
        var output = Path.Combine(_workDir, "out.ymmx");

        var result = YmmxPacker.Pack(WriteProject(shapeParameter), output, "psd");

        Assert.True(result.Success);

        using var archive = ZipFile.OpenRead(output);
        using var stream = archive.GetEntry("project.ymmp")!.Open();
        using var reader = new StreamReader(stream);

        return ((JsonObject)JsonNode.Parse(reader.ReadToEnd())!, output);
    }

    private static JsonObject Shape(JsonObject project)
        => (JsonObject)project["Items"]![0]!["ShapeParameter"]!;

    [Fact]
    public void RewritesTheGuardWhenItMatchesFilePath()
    {
        var shape = Shape(Pack(BuildShapeParameter(_psdPath)).Project);

        Assert.Equal(shape["FilePath"]!.GetValue<string>(), shape["EnableLayersFilePath"]!.GetValue<string>());
        Assert.StartsWith("assets/", shape["FilePath"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesTheLayerSelectionCount()
    {
        var shape = Shape(Pack(BuildShapeParameter(_psdPath)).Project);

        Assert.Equal(2, shape["EnableLayers"]!.AsArray().Count);
    }

    [Fact]
    public void LeavesAMismatchedGuardUntouched()
    {
        var shape = Shape(Pack(BuildShapeParameter(@"D:\somewhere_else.psd")).Project);

        Assert.Equal(@"D:\somewhere_else.psd", shape["EnableLayersFilePath"]!.GetValue<string>());
        Assert.NotEqual(shape["FilePath"]!.GetValue<string>(), shape["EnableLayersFilePath"]!.GetValue<string>());
    }

    [Fact]
    public void LeavesANullGuardAsNull()
    {
        var shape = Shape(Pack(BuildShapeParameter(null)).Project);

        Assert.Null(shape["EnableLayersFilePath"]);
    }

    [Fact]
    public void RoundTripsWithTheGuardStillMatching()
    {
        var (_, ymmxPath) = Pack(BuildShapeParameter(_psdPath));
        var destination = Path.Combine(_workDir, "extracted");

        var result = YmmxExtractor.Extract(ymmxPath, destination);
        Assert.True(result.Success);

        var extracted = (JsonObject)JsonNode.Parse(File.ReadAllText(result.YmmpPath))!;
        var shape = Shape(extracted);

        var filePath = shape["FilePath"]!.GetValue<string>();
        var guard = shape["EnableLayersFilePath"]!.GetValue<string>();

        Assert.Equal(filePath, guard);
        Assert.True(File.Exists(filePath));
        Assert.Equal(2, shape["EnableLayers"]!.AsArray().Count);
    }
}
