using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMM4CloudSync.YMMX.Core.Models;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.YMMX.Core;

public enum ExtractConflictAction
{
    Overwrite,
    CreateNew,
    Cancel
}

public class ExtractResult
{
    public bool Success { get; init; }
    public string YmmpPath { get; init; } = string.Empty;
    public string ExtractedDirectory { get; init; } = string.Empty;
    public YmmxMeta? Meta { get; init; }
}

public static class YmmxExtractor
{
    public static ExtractResult Extract(
        string ymmxPath, 
        string outputDirectory,
        Func<YmmxMeta?, YmmxMeta?, ExtractConflictAction>? conflictResolver = null)
    {
        if (!File.Exists(ymmxPath))
        {
            throw new FileNotFoundException("ymmx ファイルが見つかりません。", ymmxPath);
        }

        var finalOutputDir = outputDirectory;
        if (Directory.Exists(outputDirectory))
        {
            var existingMetaPath = Path.Combine(outputDirectory, "meta.json");
            YmmxMeta? existingMeta = null;
            
            if (File.Exists(existingMetaPath))
            {
                existingMeta = YmmxMeta.Load(existingMetaPath);
            }

            var newMeta = ReadMetaFromZip(ymmxPath);

            if (conflictResolver != null)
            {
                var action = conflictResolver(existingMeta, newMeta);
                
                switch (action)
                {
                    case ExtractConflictAction.Cancel:
                        return new ExtractResult
                        {
                            Success = false,
                            ExtractedDirectory = outputDirectory
                        };
                    
                    case ExtractConflictAction.CreateNew:
                        finalOutputDir = GetUniqueDirectory(outputDirectory);
                        break;
                    
                    case ExtractConflictAction.Overwrite:
                    default:
                        break;
                }
            }
        }

        Directory.CreateDirectory(finalOutputDir);
        
        try
        {
            ZipFile.ExtractToDirectory(ymmxPath, finalOutputDir, overwriteFiles: true);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"展開に失敗しました: {ex.Message}", ex);
        }

        var metaPath = Path.Combine(finalOutputDir, "meta.json");
        var ymmpPath = Path.Combine(finalOutputDir, "project.ymmp");

        if (!File.Exists(metaPath))
        {
            throw new InvalidDataException("meta.json が見つかりません。不正な ymmx ファイルです。");
        }

        var meta = YmmxMeta.Load(metaPath) 
            ?? throw new InvalidDataException("meta.json の読み込みに失敗しました。");

        var versionError = VersionChecker.Validate(meta);
        if (versionError != null)
        {
            try
            {
                Directory.Delete(finalOutputDir, true);
            }
            catch
            {
                // ignored
            }
            
            throw new InvalidOperationException(versionError);
        }

        if (!File.Exists(ymmpPath))
        {
            throw new InvalidDataException("project.ymmp が見つかりません。不正な ymmx ファイルです。");
        }

        RewriteToAbsolutePaths(ymmpPath, finalOutputDir);

        return new ExtractResult
        {
            Success = true,
            YmmpPath = ymmpPath,
            ExtractedDirectory = finalOutputDir,
            Meta = meta
        };
    }

    private static YmmxMeta? ReadMetaFromZip(string ymmxPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(ymmxPath);
            var metaEntry = archive.GetEntry("meta.json");
            
            if (metaEntry == null) return null;

            using var stream = metaEntry.Open();
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            
            return JsonSerializer.Deserialize<YmmxMeta>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string GetUniqueDirectory(string basePath)
    {
        var counter = 1;
        string candidate;
        
        do
        {
            candidate = $"{basePath}_{counter}";
            counter++;
        } while (Directory.Exists(candidate));

        return candidate;
    }

    private static void RewriteToAbsolutePaths(string ymmpPath, string baseDirectory)
    {
        var content = File.ReadAllText(ymmpPath);
        var json = JsonNode.Parse(content) 
            ?? throw new InvalidDataException("ymmp ファイルの解析に失敗しました。");

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