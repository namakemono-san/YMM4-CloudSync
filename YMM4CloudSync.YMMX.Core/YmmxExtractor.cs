using System.IO;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Foundation.Metadata;
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
    public bool HashMismatch { get; init; }
    public string? BackupDirectory { get; init; }
}

public static class YmmxExtractor
{
    /// <summary>
    /// Extra space to reserve when checking disk space for YMMX extraction.
    /// This accounts for decompression overhead and temporary files.
    /// </summary>
    private const long ExtraSpaceReserveBytes = 20 * 1024 * 1024; // 20MB
    
    public static ExtractResult Extract(
        string ymmxPath,
        string outputDirectory,
        Func<YmmxMeta?, YmmxMeta?, ExtractConflictAction>? conflictResolver = null)
    {
        if (!File.Exists(ymmxPath))
            throw new FileNotFoundException("ymmx ファイルが見つかりません。", ymmxPath);

        CheckDiskSpace(ymmxPath, outputDirectory);
        
        var newMeta = ReadMetaFromZip(ymmxPath);

        if (newMeta != null)
        {
            var versionError = VersionChecker.Validate(newMeta);
            if (versionError != null)
            {
                throw new InvalidOperationException(versionError);
            }
        }
        
        var finalOutputDir = outputDirectory;
        string? backupDir = null;
        
        if (Directory.Exists(outputDirectory))
        {
            var existingMetaPath = Path.Combine(outputDirectory, "meta.json");
            YmmxMeta? existingMeta = null;

            if (File.Exists(existingMetaPath))
                existingMeta = YmmxMeta.Load(existingMetaPath);

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
                        backupDir = CreateBackup(outputDirectory);
                        break;
                }
            }
            else
            {
                backupDir = CreateBackup(outputDirectory);
            }
        }

        Directory.CreateDirectory(finalOutputDir);

        try
        {
            ZipFile.ExtractToDirectory(ymmxPath, finalOutputDir, overwriteFiles: true);
        }
        catch (IOException ex)
        {
            if (DiskSpaceHelper.IsDiskFull(ex))
            {
                throw new IOException("展開中にディスクの空き領域がなくなりました。\n空き容量を確保してから再試行してください。", ex);
            }
            throw new InvalidOperationException($"展開に失敗しました: {ex.Message}", ex);
        }

        var hashMismatch = false;
        if (newMeta?.Hash != null)
        {
            var actualHash = ComputeContentHash(finalOutputDir);
        
            if (!string.Equals(newMeta.Hash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                var legacyHash = ComputeLegacyContentHashSafely(finalOutputDir);
            
                if (!string.Equals(newMeta.Hash, legacyHash, StringComparison.OrdinalIgnoreCase))
                {
                    hashMismatch = true;
                }
            }
        }

        var metaPath = Path.Combine(finalOutputDir, "meta.json");
        var ymmpPath = Path.Combine(finalOutputDir, "project.ymmp");

        if (!File.Exists(metaPath))
            throw new InvalidDataException("meta.json が見つかりません。不正な ymmx ファイルです。");

        var meta = YmmxMeta.Load(metaPath)
            ?? throw new InvalidDataException("meta.json の読み込みに失敗しました。");

        if (!File.Exists(ymmpPath))
            throw new InvalidDataException("project.ymmp が見つかりません。不正な ymmx ファイルです。");

        RewriteToAbsolutePaths(ymmpPath, finalOutputDir);

        ymmpPath = RenameYmmpToYmmxName(ymmpPath, finalOutputDir, ymmxPath);

        return new ExtractResult
        {
            Success = true,
            YmmpPath = ymmpPath,
            ExtractedDirectory = finalOutputDir,
            Meta = meta,
            HashMismatch = hashMismatch,
            BackupDirectory = backupDir
        };
    }

    private static string RenameYmmpToYmmxName(string ymmpPath, string outputDir, string ymmxPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(ymmxPath);
        var safeName = SanitizeFileName(baseName);

        if (string.IsNullOrWhiteSpace(safeName))
            return ymmpPath;

        var desired = Path.Combine(outputDir, $"{safeName}.ymmp");

        if (string.Equals(ymmpPath, desired, StringComparison.OrdinalIgnoreCase))
            return ymmpPath;

        if (File.Exists(desired))
            File.Delete(desired);

        File.Move(ymmpPath, desired);
        return desired;
    }

    private static string SanitizeFileName(string name)
    {
        name = Path.GetInvalidFileNameChars().Aggregate(name, (current, c) => current.Replace(c, '_'));

        return name.Trim();
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
                        
                        if (!absolutePath.StartsWith(Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar))
                        {
                            throw new SecurityException("Invalid file path detected");
                        }
                        
                        obj["FilePath"] = absolutePath;
                    }
                }

                foreach (var prop in obj)
                {
                    if (prop.Value != null)
                        RewritePaths(prop.Value, baseDirectory);
                }

                break;
            }
            case JsonArray arr:
            {
                foreach (var item in arr)
                {
                    if (item != null)
                        RewritePaths(item, baseDirectory);
                }

                break;
            }
        }
    }

    private static string ComputeContentHash(string directory)
    {
        long processedBytes = 0;
        return HashHelper.ComputeDirectoryHash(directory, includeLegacyFiles: false, null, 0, ref processedBytes);
    }

    private static string ComputeLegacyContentHashSafely(string directory)
    {
        long processedBytes = 0;
        return HashHelper.ComputeDirectoryHash(directory, includeLegacyFiles: true, null, 0, ref processedBytes);
    }
    
    private static string? CreateBackup(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupDir = $"{directory}_bak_{timestamp}";
            
            Directory.Move(directory, backupDir);
            return backupDir;
        }
        catch (Exception ex)
        {
            throw new IOException($"バックアップの作成に失敗しました。\n{ex.Message}", ex);
        }
    }
    
    private static void CheckDiskSpace(string ymmxPath, string outputDir)
    {
        long totalSize = 0;
        try
        {
            using var archive = ZipFile.OpenRead(ymmxPath);
            totalSize = archive.Entries.Sum(e => e.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YmmxExtractor] Failed to read archive size: {ex.Message}");
        }

        var required = totalSize + ExtraSpaceReserveBytes;
        
        DiskSpaceHelper.EnsureFreeSpace(outputDir, required, "展開先");
    }
}
