using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMM4CloudSync.YMMX.Core.Commons;
using YMM4CloudSync.YMMX.Core.Models;

namespace YMM4CloudSync.YMMX.Core;

public static class YmmxPacker
{
    private static readonly Dictionary<string, string> TypeToFolder = new()
    {
        { "YukkuriMovieMaker.Project.Items.VideoItem", "videos" },
        { "YukkuriMovieMaker.Project.Items.ImageItem", "images" },
        { "YukkuriMovieMaker.Project.Items.AudioItem", "audio" },
    };

    // TODO: 導入しているプラグインも取得してパッケージングしたい
    public static void Pack(string ymmpPath, string outputYmmxPath, string projectName)
    {
        var ymmpContent = File.ReadAllText(ymmpPath);
        var json = JsonNode.Parse(ymmpContent)!;

        var tempDir = Path.Combine(Path.GetTempPath(), $"ymmx_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var assetsDir = Path.Combine(tempDir, "assets");
            Directory.CreateDirectory(assetsDir);

            var filePaths = new Dictionary<string, string>();
            CollectAndRewritePaths(json, assetsDir, filePaths);

            foreach (var (original, newPath) in filePaths)
            {
                if (!File.Exists(original)) continue;
                var destDir = Path.GetDirectoryName(newPath)!;
                Directory.CreateDirectory(destDir);
                File.Copy(original, newPath, overwrite: true);
            }

            var ymmpOutputPath = Path.Combine(tempDir, "project.ymmp");
            File.WriteAllText(ymmpOutputPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var meta = new YmmxMeta
            {
                Id = Guid.NewGuid().ToString(),
                Name = projectName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                FormatVersion = 1,
                PluginVersion = VersionChecker.CurrentVersion,
                MinPluginVersion = VersionChecker.CurrentVersion
            };
            meta.Save(Path.Combine(tempDir, "meta.json"));

            if (File.Exists(outputYmmxPath))
            {
                File.Delete(outputYmmxPath);
            }
            ZipFile.CreateFromDirectory(tempDir, outputYmmxPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private static void CollectAndRewritePaths(JsonNode node, string assetsDir, Dictionary<string, string> filePaths)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                string? folder = null;
                if (obj.TryGetPropertyValue("$type", out var typeNode))
                {
                    var typeName = typeNode?.GetValue<string>()?.Split(',')[0];
                    if (typeName != null && TypeToFolder.TryGetValue(typeName, out var f))
                    {
                        folder = f;
                    }
                }

                if (obj.TryGetPropertyValue("FilePath", out var filePathNode) && filePathNode != null)
                {
                    var originalPath = filePathNode.GetValue<string>();
                    if (!string.IsNullOrEmpty(originalPath) && File.Exists(originalPath))
                    {
                        var fileName = Path.GetFileName(originalPath);
                        var subFolder = folder ?? "other";
                        var relativePath = $"assets/{subFolder}/{fileName}";
                        var fullPath = Path.Combine(assetsDir, subFolder, fileName);

                        filePaths[originalPath] = fullPath;
                        obj["FilePath"] = relativePath;
                    }
                }

                foreach (var prop in obj)
                {
                    if (prop.Value != null)
                    {
                        CollectAndRewritePaths(prop.Value, assetsDir, filePaths);
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
                        CollectAndRewritePaths(item, assetsDir, filePaths);
                    }
                }

                break;
            }
        }
    }
}