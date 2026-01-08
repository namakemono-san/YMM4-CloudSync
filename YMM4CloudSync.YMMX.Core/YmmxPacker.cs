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
    // Buffer size for file operations (80KB for optimal disk I/O)
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

        var tempDir = Path.Combine(Path.GetTempPath(), $"ymmx_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var assetsDir = Path.Combine(tempDir, "assets");
            Directory.CreateDirectory(assetsDir);

            var filePaths = new Dictionary<string, string>();
            var usedFileNames = new Dictionary<string, HashSet<string>>();
            
            CollectAndRewritePaths(json, assetsDir, filePaths, usedFileNames, missingFiles);

            foreach (var (original, newPath) in filePaths)
            {
                if (!File.Exists(original))
                {
                    missingFiles.Add(original);
                    continue;
                }

                var destDir = Path.GetDirectoryName(newPath)!;
                Directory.CreateDirectory(destDir);
                
                try
                {
                    File.Copy(original, newPath, overwrite: true);
                }
                catch (IOException ex)
                {
                    warnings.Add($"ファイルのコピーに失敗: {original} - {ex.Message}");
                }
            }

            var ymmpOutputPath = Path.Combine(tempDir, "project.ymmp");
            File.WriteAllText(ymmpOutputPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var allFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            var totalBytes = allFiles.Sum(f => new FileInfo(f).Length) * 2;
            long processedBytes = 0;
            
            var contentHash = ComputeContentHash(tempDir, progress, totalBytes, ref processedBytes);

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
            meta.Save(Path.Combine(tempDir, "meta.json"));

            if (File.Exists(outputYmmxPath))
            {
                File.Delete(outputYmmxPath);
            }
            
            CreateZipWithProgress(tempDir, outputYmmxPath, progress, totalBytes, ref processedBytes);

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
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignored */ }
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
                                var subFolderPath = Path.Combine(assetsDir, subFolder);
                
                                if (!usedFileNames.TryGetValue(subFolder, out var usedNames))
                                {
                                    usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    usedFileNames[subFolder] = usedNames;
                                }

                                var uniqueFileName = GetUniqueFileName(normalizedPath, usedNames, subFolderPath);
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

    private static string GetUniqueFileName(string originalPath, HashSet<string> usedNames, string targetDir)
    {
        var fileName = Path.GetFileName(originalPath);
        
        if (!usedNames.Contains(fileName) && !File.Exists(Path.Combine(targetDir, fileName)))
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
        } while (usedNames.Contains(candidate) || File.Exists(Path.Combine(targetDir, candidate)));

        return candidate;
    }

    private static string ComputeContentHash(string directory, IProgress<double>? progress, long totalJobBytes, ref long processedBytes)
    {
        return HashHelper.ComputeDirectoryHash(directory, includeLegacyFiles: false, progress, totalJobBytes, ref processedBytes);
    }
    
    private static void CreateZipWithProgress(string sourceDir, string outputZipPath, IProgress<double>? progress, long totalJobBytes, ref long processedBytes)
    {
        sourceDir = Path.GetFullPath(sourceDir);

        if (!sourceDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
            sourceDir += Path.DirectorySeparatorChar;

        using var zipToOpen = new FileStream(outputZipPath, FileMode.Create);
        using var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create);
        
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        var buffer = new byte[FileBufferSize];

        foreach (var file in files)
        {
            var relativePath = file[sourceDir.Length..].Replace('\\', '/');
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

            using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read);
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
