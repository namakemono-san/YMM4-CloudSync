using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMM4CloudSync.YMMX.Core.Commons;
using YMM4CloudSync.YMMX.Core.Models;

namespace YMM4CloudSync.YMMX.Core;

public class PackResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public List<string> MissingFiles { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public static class YmmxPacker
{
    private const int FileBufferSize = 81920;
    
    private static readonly Dictionary<string, string> TypeToFolder = new()
    {
        { "YukkuriMovieMaker.Project.Items.VideoItem", "videos" },
        { "YukkuriMovieMaker.Project.Items.ImageItem", "images" },
        { "YukkuriMovieMaker.Project.Items.AudioItem", "audio" },
    };

    public static PackResult Pack(string ymmpPath, string outputYmmxPath, string projectName, IProgress<double>? progress = null)
    {
        if (!File.Exists(ymmpPath))
        {
            throw new FileNotFoundException("ymmp ファイルが見つかりません。", ymmpPath);
        }
        
        var missingFiles = new List<string>();
        var warnings = new List<string>();

        var ymmpContent = File.ReadAllText(ymmpPath);
        var json = JsonNode.Parse(ymmpContent) 
                   ?? throw new InvalidDataException("ymmp ファイルの解析に失敗しました。");

        var tempMetaDir = Path.Combine(Path.GetTempPath(), $"ymmx_meta_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempMetaDir);
        
        try
        {
            var virtualRoot = Path.Combine(Path.GetTempPath(), $"ymmx_{Guid.NewGuid()}");
            var assetsDir = Path.Combine(virtualRoot, "assets");

            var filePaths = new Dictionary<string, string>();
            var usedFileNames = new Dictionary<string, HashSet<string>>();
            
            CollectAndRewritePaths(json, assetsDir, filePaths, usedFileNames, missingFiles);

            var packList = new List<(string Source, string RelativeDest)>();
            
            foreach (var (original, newPath) in filePaths)
            {
                if (!File.Exists(original))
                {
                    missingFiles.Add(original);
                    continue;
                }

                var relativeDest = Path.GetRelativePath(virtualRoot, newPath).Replace('\\', '/');
                packList.Add((original, relativeDest));
            }

            var ymmpOutputPath = Path.Combine(tempMetaDir, "project.ymmp");
            File.WriteAllText(ymmpOutputPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            packList.Add((ymmpOutputPath, "project.ymmp"));
            
            packList.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativeDest, b.RelativeDest));
            
            var totalContentSize = packList.Sum(f => new FileInfo(f.Source).Length);
            var required = totalContentSize + 20 * 1024 * 1024;
            
            DiskSpaceHelper.EnsureFreeSpace(outputYmmxPath, required);
            
            var totalBytes = packList.Sum(f => new FileInfo(f.Source).Length) * 2;
            long processedBytes = 0;
            
            var contentHash = ComputeContentHashFromList(packList, progress, totalBytes, ref processedBytes);

            var meta = new YmmxMeta
            {
                Id = Guid.NewGuid().ToString(),
                Name = projectName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                FormatVersion = 1,
                PluginVersion = VersionChecker.CurrentVersion,
                MinPluginVersion = VersionChecker.CurrentVersion,
                Hash = contentHash
            };
            
            var metaPath = Path.Combine(tempMetaDir, "meta.json");
            meta.Save(metaPath);

            if (File.Exists(outputYmmxPath))
            {
                File.Delete(outputYmmxPath);
            }
            
            var finalZipList = new List<(string Source, string RelativeDest)>(packList)
            {
                (metaPath, "meta.json")
            };

            CreateZipFromList(finalZipList, outputYmmxPath, progress, totalBytes, ref processedBytes);
            
            return new PackResult
            {
                Success = true,
                OutputPath = outputYmmxPath,
                MissingFiles = missingFiles,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"パッケージの作成に失敗しました: {ex.Message}", ex);
        }
        finally
        {
            if (Directory.Exists(tempMetaDir))
            {
                try { Directory.Delete(tempMetaDir, true); } catch { /* ignored */ }
            }
        }
    }

    private static void CollectAndRewritePaths(
        JsonNode node, 
        string assetsDir, 
        Dictionary<string, string> filePaths,
        Dictionary<string, HashSet<string>> usedFileNames,
        List<string> missingFiles,
        bool isRoot = true)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                obj.Remove("LayoutXml");
                obj.Remove("ToolStates");
                
                string? folder = null;
                if (obj.TryGetPropertyValue("$type", out var typeNode))
                {
                    var typeName = typeNode?.GetValue<string>().Split(',')[0];
                    if (typeName != null && TypeToFolder.TryGetValue(typeName, out var f))
                    {
                        folder = f;
                    }
                }

                if (!isRoot && obj.TryGetPropertyValue("FilePath", out var filePathNode) && filePathNode != null)
                {
                    var originalPath = filePathNode.GetValue<string>();
                    if (!string.IsNullOrEmpty(originalPath))
                    {
                        var normalizedPath = Path.GetFullPath(originalPath);
                        
                        if (filePaths.TryGetValue(normalizedPath, out var existingFullPath))
                        {
                            var relativeFromAssets = Path.GetRelativePath(assetsDir, existingFullPath);
                            var relativePath = $"assets/{relativeFromAssets}".Replace("\\", "/");
                            obj["FilePath"] = relativePath;
                        }
                        else
                        {
                            if (!File.Exists(normalizedPath))
                            {
                                if (!missingFiles.Contains(normalizedPath))
                                    missingFiles.Add(normalizedPath);
                            }
                            else
                            {
                                var subFolder = folder ?? "other";
                                Path.Combine(assetsDir, subFolder);
                
                                if (!usedFileNames.TryGetValue(subFolder, out var usedNames))
                                {
                                    usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    usedFileNames[subFolder] = usedNames;
                                }

                                var uniqueFileName = GetUniqueFileName(normalizedPath, usedNames);
                                usedNames.Add(uniqueFileName);

                                var relativePath = $"assets/{subFolder}/{uniqueFileName}";
                                var fullPath = Path.Combine(assetsDir, subFolder, uniqueFileName);

                                filePaths[normalizedPath] = fullPath;
                                obj["FilePath"] = relativePath;
                            }
                        }
                    }
                }

                foreach (var prop in obj)
                {
                    if (prop.Value != null)
                    {
                        CollectAndRewritePaths(prop.Value, assetsDir, filePaths, usedFileNames, missingFiles, false);
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
                        CollectAndRewritePaths(item, assetsDir, filePaths, usedFileNames, missingFiles, false);
                    }
                }

                break;
            }
        }
    }

    private static string GetUniqueFileName(string originalPath, HashSet<string> usedNames)
    {
        var fileName = Path.GetFileName(originalPath);
        
        if (!usedNames.Contains(fileName))
        {
            return fileName;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 1;

        string candidate;
        do
        {
            candidate = $"{baseName}_{counter}{ext}";
            counter++;
        } while (usedNames.Contains(candidate));

        return candidate;
    }

    private static string ComputeContentHashFromList(
        List<(string Source, string RelativeDest)> fileList, 
        IProgress<double>? progress, 
        long totalJobBytes, 
        ref long processedBytes)
    {
        using var sha256 = SHA256.Create();
        var buffer = new byte[FileBufferSize];

        foreach (var (source, relativeDest) in fileList)
        {
            if (relativeDest.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase)) continue;
            
            var pathBytes = Encoding.UTF8.GetBytes(relativeDest);
            sha256.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read);
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);

                if (progress == null || totalJobBytes <= 0) continue;

                processedBytes += bytesRead;
                progress.Report((double)processedBytes / totalJobBytes * 100);
            }
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }
    
    private static void CreateZipFromList(
        List<(string Source, string RelativeDest)> fileList,
        string outputZipPath, 
        IProgress<double>? progress, 
        long totalJobBytes, 
        ref long processedBytes)
    {
        var dir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var zipToOpen = new FileStream(outputZipPath, FileMode.Create);
        using var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create);
        
        var buffer = new byte[FileBufferSize];

        foreach (var (source, relativeDest) in fileList)
        {
            var entry = archive.CreateEntry(relativeDest, CompressionLevel.Optimal);

            using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read);
            using var entryStream = entry.Open();
            
            int bytesRead;
            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryStream.Write(buffer, 0, bytesRead);

                if (progress == null || totalJobBytes <= 0) continue;
                
                processedBytes += bytesRead;
                progress.Report((double)processedBytes / totalJobBytes * 100);
            }
        }
    }
}
