using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMM4CloudSync.YMMX.Core.Commons;
using YMM4CloudSync.YMMX.Core.Models;

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
    public List<string> ExternalReferences { get; init; } = [];
}

public static class YmmxExtractor
{
    /// <summary>
    /// Extra space to reserve when checking disk space for YMMX extraction.
    /// This accounts for decompression overhead and temporary files.
    /// </summary>
    private const long ExtraSpaceReserveBytes = 20 * 1024 * 1024; // 20MB

    private const int MaxEntryCount = 100_000;

    private const long MaxUncompressedBytes = 64L * 1024 * 1024 * 1024; // 64GB

    private const long MaxCompressionRatio = 100;

    public static ExtractResult Extract(
        string ymmxPath,
        string outputDirectory,
        Func<YmmxMeta?, YmmxMeta?, ExtractConflictAction>? conflictResolver = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ymmxPath))
            throw new FileNotFoundException("ymmx ファイルが見つかりません。", ymmxPath);

        cancellationToken.ThrowIfCancellationRequested();

        var totalSize = ValidateArchive(ymmxPath);

        CheckDiskSpace(totalSize, outputDirectory);

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
        var updateInPlace = false;
        
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
                        (backupDir, updateInPlace) = CreateBackupForUpdate(outputDirectory);
                        break;
                }
            }
            else
            {
                (backupDir, updateInPlace) = CreateBackupForUpdate(outputDirectory);
            }
        }

        Directory.CreateDirectory(finalOutputDir);

        try
        {
            try
            {
                if (updateInPlace && !TryUpdateArchiveInPlace(ymmxPath, finalOutputDir, cancellationToken))
                {
                    if (backupDir != null) DeleteDirectoryQuietly(backupDir);

                    updateInPlace = false;
                    backupDir = CreateBackup(finalOutputDir);

                    Directory.CreateDirectory(finalOutputDir);
                }

                if (!updateInPlace) ExtractArchive(ymmxPath, finalOutputDir, cancellationToken);
            }
            catch (IOException ex)
            {
                if (DiskSpaceHelper.IsDiskFull(ex))
                {
                    throw new IOException("展開中にディスクの空き領域がなくなりました。\n空き容量を確保してから再試行してください。", ex);
                }
                throw new InvalidOperationException($"展開に失敗しました: {ex.Message}", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var hashMismatch = false;
            if (!updateInPlace && newMeta?.Hash != null)
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

            var externalReferences = RewriteToAbsolutePaths(ymmpPath, finalOutputDir);

            ymmpPath = RenameYmmpToYmmxName(ymmpPath, finalOutputDir, ymmxPath);

            return new ExtractResult
            {
                Success = true,
                YmmpPath = ymmpPath,
                ExtractedDirectory = finalOutputDir,
                Meta = meta,
                HashMismatch = hashMismatch,
                BackupDirectory = backupDir,
                ExternalReferences = externalReferences
            };
        }
        catch
        {
            RollBack(finalOutputDir, backupDir, outputDirectory);
            throw;
        }
    }

    private static (string? BackupDirectory, bool CanUpdateInPlace) CreateBackupForUpdate(string directory)
    {
        var snapshot = TryCreateLinkedSnapshot(directory);

        return snapshot != null ? (snapshot, true) : (CreateBackup(directory), false);
    }

    private static string? TryCreateLinkedSnapshot(string directory)
    {
        var backupDir = $"{directory}_bak_{DateTime.Now:yyyyMMdd_HHmmss}";

        if (Directory.Exists(backupDir)) return null;

        try
        {
            Directory.CreateDirectory(backupDir);

            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(backupDir, Path.GetRelativePath(directory, file));

                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                if (HardLink.TryCreate(target, file)) continue;

                DeleteDirectoryQuietly(backupDir);
                return null;
            }

            return backupDir;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxExtractor] Snapshot backup failed: {ex.Message}");
            DeleteDirectoryQuietly(backupDir);
            return null;
        }
    }

    private static bool TryUpdateArchiveInPlace(string ymmxPath, string destinationDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destinationDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(ymmxPath);

        var buffer = new byte[1024 * 1024];

        var toWrite = new List<(ZipArchiveEntry Entry, string TargetPath)>();

        var toDelete = new HashSet<string>(
            Directory.GetFiles(destinationDirectory, "*", SearchOption.AllDirectories),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name)) continue;

            var targetPath = Path.GetFullPath(Path.Combine(root, entry.FullName));

            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"ymmx ファイルに展開先の外を指すエントリが含まれています: {entry.FullName}");
            }

            toDelete.Remove(targetPath);

            if (IsUpToDate(entry, targetPath, buffer, cancellationToken)) continue;

            toWrite.Add((entry, targetPath));
        }

        var renamedProjectPath = Path.Combine(root, Path.GetFileNameWithoutExtension(ymmxPath) + ".ymmp");

        if (!CanReplaceAll(toWrite.Select(x => x.TargetPath).Concat(toDelete).Append(renamedProjectPath)))
        {
            return false;
        }

        try
        {
            foreach (var (entry, targetPath) in toWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                if (File.Exists(targetPath)) File.Delete(targetPath);

                using var source = entry.Open();
                using var target = new FileStream(
                    targetPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length);

                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                }
            }

            foreach (var leftover in toDelete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                File.Delete(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !DiskSpaceHelper.IsDiskFull(ex))
        {
            Debug.WriteLine($"[YmmxExtractor] In-place update failed after planning: {ex.Message}");
            return false;
        }

        RemoveEmptyDirectories(destinationDirectory);

        return true;
    }

    private static bool CanReplaceAll(IEnumerable<string> paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;

            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[YmmxExtractor] {path} is locked, falling back to a full extract.");
                return false;
            }
        }

        return true;
    }

    private static bool IsUpToDate(ZipArchiveEntry entry, string targetPath, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(targetPath);

            if (!info.Exists || info.Length != entry.Length) return false;

            return Crc32.ComputeFile(targetPath, buffer, cancellationToken) == entry.Crc32;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxExtractor] Failed to compare {targetPath}: {ex.Message}");
            return false;
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;

                Directory.Delete(directory);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YmmxExtractor] Failed to remove empty directory: {ex.Message}");
            }
        }
    }

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxExtractor] Failed to delete {directory}: {ex.Message}");
        }
    }

    private static void ExtractArchive(string ymmxPath, string destinationDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destinationDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(ymmxPath);

        var buffer = new byte[1024 * 1024];

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name)) continue;

            var targetPath = Path.GetFullPath(Path.Combine(root, entry.FullName));

            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"ymmx ファイルに展開先の外を指すエントリが含まれています: {entry.FullName}");
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            using var source = entry.Open();
            using var target = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length);

            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
            }
        }
    }

    private static void RollBack(string extractedDirectory, string? backupDirectory, string originalDirectory)
    {
        try
        {
            if (Directory.Exists(extractedDirectory))
                Directory.Delete(extractedDirectory, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxExtractor] Failed to remove partial output: {ex.Message}");
        }

        if (backupDirectory == null || !Directory.Exists(backupDirectory)) return;

        try
        {
            if (!Directory.Exists(originalDirectory))
                Directory.Move(backupDirectory, originalDirectory);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YmmxExtractor] Failed to restore backup: {ex.Message}");
        }
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

    private const string UnavailableAssetFolder = "_unavailable";

    private static List<string> RewriteToAbsolutePaths(string ymmpPath, string baseDirectory)
    {
        var content = File.ReadAllText(ymmpPath);
        var json = JsonNode.Parse(content)
            ?? throw new InvalidDataException("ymmp ファイルの解析に失敗しました。");

        var externalReferences = new List<string>();

        RewritePaths(json, baseDirectory, externalReferences, isRoot: true);

        File.WriteAllText(ymmpPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return externalReferences;
    }

    private static void RewritePaths(JsonNode node, string baseDirectory, List<string> externalReferences, bool isRoot = false)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (!isRoot
                    && obj.TryGetPropertyValue("FilePath", out var filePathNode)
                    && filePathNode is JsonValue filePathValue
                    && filePathValue.TryGetValue<string>(out var relativePath)
                    && !string.IsNullOrEmpty(relativePath))
                {
                    var resolved = ResolveAssetPath(relativePath, baseDirectory, externalReferences);

                    obj["FilePath"] = resolved;
                    RewriteSiblingPaths(obj, relativePath, resolved);
                }

                RewriteDirectoryReferences(obj, baseDirectory, externalReferences);

                foreach (var prop in obj)
                {
                    if (prop.Value != null)
                        RewritePaths(prop.Value, baseDirectory, externalReferences);
                }

                break;
            }
            case JsonArray arr:
            {
                foreach (var item in arr)
                {
                    if (item != null)
                        RewritePaths(item, baseDirectory, externalReferences);
                }

                break;
            }
        }
    }

    /// <summary>
    /// Mirrors YmmxPacker.RewriteSiblingPaths: a sibling property that held the same value as the
    /// original (packed) FilePath is repointed to the same resolved value, so validity guards like
    /// PsdShapeParameter.EnableLayersFilePath keep matching FilePath after extraction.
    /// </summary>
    private static void RewriteSiblingPaths(JsonObject obj, string originalValue, string newValue)
    {
        List<string>? keys = null;

        foreach (var prop in obj)
        {
            if (prop.Key == "FilePath") continue;
            if (prop.Value is not JsonValue value) continue;
            if (!value.TryGetValue<string>(out var candidate)) continue;
            if (!string.Equals(candidate, originalValue, StringComparison.Ordinal)) continue;

            (keys ??= []).Add(prop.Key);
        }

        if (keys == null) return;

        foreach (var key in keys) obj[key] = newValue;
    }

    private static void RewriteDirectoryReferences(
        JsonObject obj, string baseDirectory, List<string> externalReferences)
    {
        List<KeyValuePair<string, string>>? rewrites = null;

        foreach (var prop in obj)
        {
            if (prop.Key == "FilePath") continue;
            if (prop.Value is not JsonValue value) continue;
            if (!value.TryGetValue<string>(out var declared)) continue;
            if (string.IsNullOrEmpty(declared)) continue;
            if (!declared.StartsWith($"assets/{YmmxPacker.DirectoryAssetFolder}/", StringComparison.Ordinal)) continue;

            rewrites ??= [];
            rewrites.Add(new KeyValuePair<string, string>(
                prop.Key, ResolveAssetPath(declared, baseDirectory, externalReferences)));
        }

        if (rewrites == null) return;

        foreach (var (key, resolved) in rewrites) obj[key] = resolved;
    }

    private static string ResolveAssetPath(string declaredPath, string baseDirectory, List<string> externalReferences)
    {
        if (IsPackedAssetPath(declaredPath))
        {
            var absolutePath = Path.GetFullPath(Path.Combine(baseDirectory, declaredPath));

            if (absolutePath.StartsWith(Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar))
            {
                return absolutePath;
            }
        }

        if (!externalReferences.Contains(declaredPath))
            externalReferences.Add(declaredPath);

        return NeutralizeReference(declaredPath, baseDirectory);
    }

    private static string NeutralizeReference(string declaredPath, string baseDirectory)
    {
        var safeName = PathTagResolver.SanitizeFileName(declaredPath, "unavailable");

        return Path.Combine(baseDirectory, "assets", UnavailableAssetFolder, safeName);
    }

    private static bool IsPackedAssetPath(string path)
    {
        if (!path.StartsWith("assets/", StringComparison.Ordinal)) return false;

        if (Path.IsPathRooted(path)) return false;

        var segments = path.Split('/', '\\');

        return segments.All(segment => segment != ".." && !segment.Contains(':'));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "既存のプロジェクトを退避できませんでした。\n\n" +
                "このプロジェクトを YMM4 で開いたままにしている可能性があります。\n" +
                "YMM4 で閉じてから、もう一度お試しください。", ex);
        }
        catch (Exception ex)
        {
            throw new IOException($"バックアップの作成に失敗しました。\n{ex.Message}", ex);
        }
    }
    
    private static long ValidateArchive(string ymmxPath)
    {
        ZipArchive archive;

        try
        {
            archive = ZipFile.OpenRead(ymmxPath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                "ymmx ファイルを読み込めませんでした。\nファイルが破損しているか、ymmx 形式ではありません。", ex);
        }

        using var _ = archive;

        var entryCount = archive.Entries.Count;

        if (entryCount > MaxEntryCount)
        {
            throw new InvalidDataException(
                $"ymmx ファイルに含まれるファイル数が多すぎます。({entryCount:N0} 件 / 上限 {MaxEntryCount:N0} 件)\n" +
                "不正なファイルの可能性があります。");
        }

        long uncompressed = 0;
        long compressed = 0;

        foreach (var entry in archive.Entries)
        {
            uncompressed += entry.Length;
            compressed += entry.CompressedLength;

            if (uncompressed > MaxUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"ymmx ファイルの展開後のサイズが上限 ({MaxUncompressedBytes / (1024L * 1024 * 1024)} GB) を超えます。\n" +
                    "不正なファイルの可能性があります。");
            }
        }

        if (compressed > 0 && uncompressed / compressed > MaxCompressionRatio)
        {
            throw new InvalidDataException(
                "ymmx ファイルの圧縮率が異常です。\n展開すると極端に大きくなるため、処理を中止しました。");
        }

        return uncompressed;
    }

    private static void CheckDiskSpace(long totalSize, string outputDir)
    {
        var required = totalSize + ExtraSpaceReserveBytes;

        DiskSpaceHelper.EnsureFreeSpace(outputDir, required, "展開先");
    }
}
