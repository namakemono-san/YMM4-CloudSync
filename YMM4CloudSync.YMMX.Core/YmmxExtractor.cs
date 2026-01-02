using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMM4CloudSync.YMMX.Core.Models;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.YMMX.Core;

public static class YmmxExtractor
{
    public static string Extract(string ymmxPath, string outputDirectory)
    {
        ZipFile.ExtractToDirectory(ymmxPath, outputDirectory, overwriteFiles: true);

        var metaPath = Path.Combine(outputDirectory, "meta.json");
        var ymmpPath = Path.Combine(outputDirectory, "project.ymmp");

        if (!File.Exists(metaPath))
        {
            throw new InvalidDataException("meta.json が見つかりません。");
        }

        var meta = YmmxMeta.Load(metaPath);
        if (meta == null)
        {
            throw new InvalidDataException("meta.json の読み込みに失敗しました。");
        }

        var error = VersionChecker.Validate(meta);
        if (error != null)
        {
            Directory.Delete(outputDirectory, true);
            throw new InvalidOperationException(error);
        }

        if (!File.Exists(ymmpPath))
        {
            throw new InvalidDataException("project.ymmp が見つかりません。");
        }
        
        RewriteToAbsolutePaths(ymmpPath, outputDirectory);

        return ymmpPath;
    }
    

    private static void RewriteToAbsolutePaths(string ymmpPath, string baseDirectory)
    {
        var content = File.ReadAllText(ymmpPath);
        var json = JsonNode.Parse(content)!;

        RewritePaths(json, baseDirectory);

        File.WriteAllText(ymmpPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RewritePaths(JsonNode node, string baseDirectory)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.TryGetPropertyValue("FilePath", out var filePathNode) && filePathNode != null)
                {
                    var relativePath = filePathNode.GetValue<string>();
                    if (!string.IsNullOrEmpty(relativePath) && relativePath.StartsWith("assets/"))
                    {
                        var absolutePath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
                        obj["FilePath"] = absolutePath;
                    }
                }

                foreach (var prop in obj)
                {
                    if (prop.Value != null)
                    {
                        RewritePaths(prop.Value, baseDirectory);
                    }
                }

                break;
            }
            case JsonArray arr:
            {
                foreach (var item in arr)
                {
                    if (item != null)
                    {
                        RewritePaths(item, baseDirectory);
                    }
                }

                break;
            }
        }
    }
}