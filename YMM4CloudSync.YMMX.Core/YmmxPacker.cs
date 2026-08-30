using System.Diagnostics;
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
}

public static class YmmxPacker
{
    private const int FileBufferSize = 1024 * 1024;
    
    /// <summary>
    /// Extra space to reserve when checking disk space for YMMX archive creation.
    /// This accounts for metadata, compression overhead, and temporary files.
    /// </summary>
    private const long ExtraSpaceReserveBytes = 20 * 1024 * 1024; // 20MB
    
    private static readonly Dictionary<string, string> TypeToFolder = new()
    {
        { "YukkuriMovieMaker.Project.Items.VideoItem", "videos" },
        { "YukkuriMovieMaker.Project.Items.ImageItem", "images" },
        { "YukkuriMovieMaker.Project.Items.AudioItem", "audio" },
    };

    internal const string DirectoryAssetFolder = "tachie";

    internal const long MaxDirectoryBytes = 512L * 1024 * 1024;

    internal const int MaxDirectoryFiles = 20000;

    public static PackResult Pack(string ymmpPath, string outputYmmxPath, string projectName,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ymmpPath))
        {
            throw new FileNotFoundException("ymmp ファイルが見つかりません。", ymmpPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var missingFiles = new List<string>();

        var ymmpContent = File.ReadAllText(ymmpPath);
        var json = JsonNode.Parse(ymmpContent) 
                   ?? throw new InvalidDataException("ymmp ファイルの解析に失敗しました。");

        var tempMetaDir = Path.Combine(Path.GetTempPath(), $"ymmx_meta_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempMetaDir);
        
        try
        {
            var virtualRoot = Path.Combine(Path.GetTempPath(), $"ymmx_{Guid.NewGuid()}");
            var assetsDir = Path.Combine(virtualRoot, "assets");

            var filePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedFileNames = new Dictionary<string, HashSet<string>>();
            var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectDirectories(json, directories, usedDirectoryNames, missingFiles);
            CollectAndRewritePaths(json, assetsDir, filePaths, usedFileNames, missingFiles, directories);

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

            foreach (var (source, destName) in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var remainder = Path.GetRelativePath(source, file).Replace('\\', '/');

                    packList.Add((file, $"assets/{DirectoryAssetFolder}/{destName}/{remainder}"));
                }
            }

            var ymmpOutputPath = Path.Combine(tempMetaDir, "project.ymmp");
            File.WriteAllText(ymmpOutputPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            packList.Add((ymmpOutputPath, "project.ymmp"));
            
            packList.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativeDest, b.RelativeDest));
            
            var totalContentSize = packList.Sum(f => new FileInfo(f.Source).Length);
            var required = totalContentSize + ExtraSpaceReserveBytes;

            DiskSpaceHelper.EnsureFreeSpace(outputYmmxPath, required);

            if (File.Exists(outputYmmxPath))
            {
                File.Delete(outputYmmxPath);
            }

            try
            {
                WriteArchive(packList, outputYmmxPath, projectName, totalContentSize, progress, cancellationToken);
            }
            catch
            {
                DeleteQuietly(outputYmmxPath);
                throw;
            }

            return new PackResult
            {
                Success = true,
                OutputPath = outputYmmxPath,
                MissingFiles = missingFiles
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"パッケージの作成に失敗しました: {ex.Message}", ex);
        }
        finally
        {
            if (Directory.Exists(tempMetaDir))
            {
                try 
                { 
                    Directory.Delete(tempMetaDir, true); 
                } 
                catch (Exception ex)
                { 
                    Debug.WriteLine($"[YmmxPacker] Failed to delete temporary directory: {ex.Message}");
                }
            }
        }
    }

    internal static void CollectDirectories(
        JsonNode node,
        Dictionary<string, string> directories,
        HashSet<string> usedNames,
        List<string> oversizedDirectories)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (IsTachieParameter(obj))
                {
                    foreach (var prop in obj)
                    {
                        if (prop.Key != "Directory") continue;
                        if (prop.Value is not JsonValue value) continue;
                        if (!value.TryGetValue<string>(out var declared)) continue;

                        RegisterDirectory(declared, directories, usedNames, oversizedDirectories);
                    }
                }

                foreach (var prop in obj)
                {
                    if (prop.Value != null)
                        CollectDirectories(prop.Value, directories, usedNames, oversizedDirectories);
                }

                break;
            }
            case JsonArray arr:
            {
                foreach (var item in arr)
                {
                    if (item != null)
                        CollectDirectories(item, directories, usedNames, oversizedDirectories);
                }

                break;
            }
        }
    }

    private static bool IsTachieParameter(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("$type", out var typeNode)) return false;
        if (typeNode is not JsonValue typeValue) return false;
        if (!typeValue.TryGetValue<string>(out var typeName)) return false;

        return typeName.Contains("Tachie", StringComparison.Ordinal);
    }

    private static void RegisterDirectory(
        string? declared,
        Dictionary<string, string> directories,
        HashSet<string> usedNames,
        List<string> oversizedDirectories)
    {
        if (string.IsNullOrWhiteSpace(declared)) return;
        if (!Path.IsPathRooted(declared)) return;

        string normalized;

        try
        {
            normalized = Path.GetFullPath(declared).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxPacker] Invalid directory reference: {ex.Message}");
            return;
        }

        if (directories.ContainsKey(normalized)) return;
        if (!Directory.Exists(normalized)) return;
        if (Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar) == normalized) return;

        if (!IsWithinLimits(normalized))
        {
            if (!oversizedDirectories.Contains(normalized)) oversizedDirectories.Add(normalized);
            return;
        }

        var name = PathTagResolver.SanitizeFileName(Path.GetFileName(normalized), "tachie");
        var unique = name;
        var counter = 1;

        while (!usedNames.Add(unique))
        {
            unique = $"{name}_{counter}";
            counter++;
        }

        directories[normalized] = unique;
    }

    private static bool IsWithinLimits(string directory)
    {
        try
        {
            long total = 0;
            var count = 0;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                count++;
                if (count > MaxDirectoryFiles) return false;

                total += new FileInfo(file).Length;
                if (total > MaxDirectoryBytes) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxPacker] Failed to measure {directory}: {ex.Message}");
            return false;
        }
    }

    internal static string? TryRewriteUnderDirectory(string declared, Dictionary<string, string> directories)
    {
        if (string.IsNullOrWhiteSpace(declared)) return null;
        if (!Path.IsPathRooted(declared)) return null;

        string normalized;

        try
        {
            normalized = Path.GetFullPath(declared).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxPacker] Invalid path reference: {ex.Message}");
            return null;
        }

        foreach (var (source, destName) in directories)
        {
            if (string.Equals(normalized, source, StringComparison.OrdinalIgnoreCase))
                return $"assets/{DirectoryAssetFolder}/{destName}";

            if (!normalized.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = normalized[(source.Length + 1)..].Replace('\\', '/');

            return $"assets/{DirectoryAssetFolder}/{destName}/{remainder}";
        }

        return null;
    }

    private static void CollectAndRewritePaths(
        JsonNode node,
        string assetsDir,
        Dictionary<string, string> filePaths,
        Dictionary<string, HashSet<string>> usedFileNames,
        List<string> missingFiles,
        Dictionary<string, string> directories,
        bool isRoot = true)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                obj.Remove("LayoutXml");
                obj.Remove("ToolStates");

                RewriteDirectoryReferences(obj, directories);

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
                    if (!string.IsNullOrEmpty(originalPath) && Path.IsPathRooted(originalPath))
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
                            var subFolder = folder ?? "other";

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

                foreach (var prop in obj)
                {
                    if (prop.Value != null)
                    {
                        CollectAndRewritePaths(prop.Value, assetsDir, filePaths, usedFileNames, missingFiles,
                            directories, false);
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
                        CollectAndRewritePaths(item, assetsDir, filePaths, usedFileNames, missingFiles,
                            directories, false);
                    }
                }

                break;
            }
        }
    }

    private static void RewriteDirectoryReferences(JsonObject obj, Dictionary<string, string> directories)
    {
        if (directories.Count == 0) return;

        List<KeyValuePair<string, string>>? rewrites = null;

        foreach (var prop in obj)
        {
            if (prop.Value is not JsonValue value) continue;
            if (!value.TryGetValue<string>(out var declared)) continue;
            if (TryRewriteUnderDirectory(declared, directories) is not { } rewritten) continue;

            rewrites ??= [];
            rewrites.Add(new KeyValuePair<string, string>(prop.Key, rewritten));
        }

        if (rewrites == null) return;

        foreach (var (key, rewritten) in rewrites) obj[key] = rewritten;
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

    private static void WriteArchive(
        List<(string Source, string RelativeDest)> packList,
        string outputZipPath,
        string projectName,
        long totalJobBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var zipToOpen = new FileStream(
            outputZipPath, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize);
        using var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[FileBufferSize];
        long processedBytes = 0;

        foreach (var (source, relativeDest) in packList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            hash.AppendData(Encoding.UTF8.GetBytes(relativeDest));

            var entry = archive.CreateEntry(relativeDest, ZipCompressionPolicy.ForEntry(relativeDest));

            using var sourceStream = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, FileOptions.SequentialScan);
            using var entryStream = entry.Open();

            int bytesRead;
            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                hash.AppendData(buffer, 0, bytesRead);
                entryStream.Write(buffer, 0, bytesRead);

                if (progress == null || totalJobBytes <= 0) continue;

                processedBytes += bytesRead;
                progress.Report(Math.Min(99.9, (double)processedBytes / totalJobBytes * 100));
            }
        }

        var contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

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

        var metaEntry = archive.CreateEntry("meta.json", CompressionLevel.Optimal);

        using (var metaStream = metaEntry.Open())
        {
            var metaBytes = Encoding.UTF8.GetBytes(meta.ToJson());
            metaStream.Write(metaBytes, 0, metaBytes.Length);
        }

        progress?.Report(100.0);
    }

    private static void DeleteQuietly(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxPacker] Failed to delete {path}: {ex.Message}");
        }
    }
}
