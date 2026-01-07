# Recommended Code Improvements

This document provides specific code examples for the most important improvements identified in the code review.

## 1. Critical: Implement IDisposable in GoogleDriveService

### Current Code (GoogleDriveService.cs)
```csharp
public class GoogleDriveService : ICloudStorageService
{
    private DriveService? _driveService;
    
    public async Task LogoutAsync()
    {
        // ... cleanup code ...
        _driveService?.Dispose();
        _driveService = null;
    }
}
```

### Recommended Code
```csharp
public class GoogleDriveService : ICloudStorageService, IDisposable
{
    private DriveService? _driveService;
    private bool _disposed;
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            _driveService?.Dispose();
            _driveService = null;
        }
        
        _disposed = true;
    }
    
    public async Task LogoutAsync()
    {
        if (Directory.Exists(CredentialPath))
        {
            Directory.Delete(CredentialPath, true);
        }

        _driveService?.Dispose();
        _driveService = null;
        _appFolderId = null;

        await Task.CompletedTask;
    }
}
```

**Why:** Ensures `DriveService` is properly disposed even if `LogoutAsync()` is never called.

---

## 2. Critical: Add Exception Handling to Async Void Event Handlers

### Current Code (ToolView.xaml.cs)
```csharp
SelectedCloudService.Subscribe(async void (_) =>
{
    CloudFilesList.ItemsSource = null;
    var ok = IsConnected;
    UploadButton.IsEnabled = ok;
    RefreshButton.IsEnabled = ok;

    if (ok)
        await RefreshFileListAsync();
});
```

### Recommended Code
```csharp
SelectedCloudService.Subscribe(async void (_) =>
{
    try
    {
        CloudFilesList.ItemsSource = null;
        var ok = IsConnected;
        UploadButton.IsEnabled = ok;
        RefreshButton.IsEnabled = ok;

        if (ok)
            await RefreshFileListAsync();
    }
    catch (Exception ex)
    {
        SentrySdk.CaptureException(ex);
        MessageBox.Show(
            "サービス切り替え中にエラーが発生しました。", 
            "エラー",
            MessageBoxButton.OK, 
            MessageBoxImage.Error);
    }
});
```

**Why:** Prevents unhandled exceptions in async void methods from crashing the application.

---

## 3. Critical: Add Logging to Swallowed Exceptions

### Current Code (Plugin.cs)
```csharp
private void CleanUpTempFiles()
{
    try
    {
        var tempPath = Path.GetTempPath();
        var directories = Directory.GetDirectories(tempPath, "ymmx_*");

        foreach (var dir in directories)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
            }
        }
    }
    catch
    {
        // ignored
    }
}
```

### Recommended Code
```csharp
private void CleanUpTempFiles()
{
    try
    {
        var tempPath = Path.GetTempPath();
        var directories = Directory.GetDirectories(tempPath, "ymmx_*");

        foreach (var dir in directories)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                // Log but don't crash - this is cleanup code
                Debug.WriteLine($"[CleanUp] Failed to delete temp directory {dir}: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[CleanUp] Failed to enumerate temp directories: {ex.Message}");
    }
}
```

**Why:** Makes debugging easier by logging failures without crashing the application.

---

## 4. High Priority: Remove Unnecessary Catch-Rethrow Blocks

### Current Code (GoogleDriveService.cs)
```csharp
public async Task<string> UploadFileAsync(string localPath, string remoteName, IProgress<double>? progress = null)
{
    // ... validation ...
    
    try
    {
        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            // ... upload logic ...
        });
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception)
    {
        throw;
    }
}
```

### Recommended Code
```csharp
public async Task<string> UploadFileAsync(string localPath, string remoteName, IProgress<double>? progress = null)
{
    if (_driveService == null)
        throw new InvalidOperationException("認証されていません。");

    if (!File.Exists(localPath))
        throw new FileNotFoundException("アップロードするファイルが見つかりません。", localPath);

    return await RetryHelper.ExecuteWithRetryAsync(async () =>
    {
        var existingFileId = await FindFileByNameAsync(remoteName);

        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
        var totalSize = stream.Length;

        Google.Apis.Upload.IUploadProgress result;
        string fileId;

        if (existingFileId != null)
        {
            // ... update logic ...
        }
        else
        {
            // ... create logic ...
        }

        if (result.Status != Google.Apis.Upload.UploadStatus.Completed)
            throw new Exception($"アップロードに失敗しました: {result.Exception?.Message}");

        return fileId;
    });
}
```

**Why:** Simplifies code by removing catch blocks that don't add value.

---

## 5. Medium Priority: Extract Duplicate Hash Computation Logic

### Current Code
Hash computation logic is duplicated in:
- `YmmxExtractor.cs` - `ComputeContentHash()` and `ComputeLegacyContentHashSafely()`
- `YmmxPacker.cs` - `ComputeContentHash()`

### Recommended Code

Create a new file: `YMM4CloudSync.YMMX.Core/Commons/HashHelper.cs`

```csharp
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class HashHelper
{
    private const int FileBufferSize = 81920; // 80KB

    /// <summary>
    /// Computes SHA256 hash of directory contents, excluding meta.json and system files.
    /// Files are processed in deterministic order for consistent hashing.
    /// </summary>
    public static string ComputeDirectoryHash(
        string directory, 
        bool includeLegacyFiles = false,
        IProgress<double>? progress = null,
        long totalBytes = 0,
        ref long processedBytes)
    {
        using var sha256 = SHA256.Create();
        
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase));

        if (!includeLegacyFiles)
        {
            files = files
                .Where(f => !Path.GetFileName(f).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase));
        }

        files = files.OrderBy(f => f, StringComparer.Ordinal);

        var buffer = new byte[FileBufferSize];

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            sha256.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);

                if (progress != null && totalBytes > 0)
                {
                    processedBytes += bytesRead;
                    progress.Report((double)processedBytes / totalBytes * 100);
                }
            }
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }
}
```

Then update the existing files:

**YmmxExtractor.cs**
```csharp
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
```

**YmmxPacker.cs**
```csharp
private static string ComputeContentHash(string directory, IProgress<double>? progress, long totalJobBytes, ref long processedBytes)
{
    return HashHelper.ComputeDirectoryHash(directory, includeLegacyFiles: false, progress, totalJobBytes, ref processedBytes);
}
```

**Why:** Reduces code duplication, makes maintenance easier, and ensures consistent hash computation across the codebase.

---

## 6. Medium Priority: Add XML Documentation to Public APIs

### Current Code (ICloudStorageService.cs)
```csharp
public interface ICloudStorageService
{
    string ServiceName { get; }
    bool IsAuthenticated { get; }

    Task<bool> AuthenticateAsync();
    Task LogoutAsync();

    Task<List<CloudFile>> ListFilesAsync(string? folderId = null);
    Task<string> UploadFileAsync(string localPath, string remotePath, IProgress<double>? progress = null);
    Task DownloadFileAsync(string remoteFileId, string localPath, IProgress<double>? progress = null);
    Task DeleteFileAsync(string fileId);
}
```

### Recommended Code
```csharp
/// <summary>
/// Interface for cloud storage service providers.
/// </summary>
public interface ICloudStorageService
{
    /// <summary>
    /// Gets the display name of the cloud service (e.g., "Google Drive", "OneDrive").
    /// </summary>
    string ServiceName { get; }

    /// <summary>
    /// Gets a value indicating whether the user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Attempts to authenticate silently using cached credentials.
    /// </summary>
    /// <returns>True if authentication succeeded; otherwise, false.</returns>
    Task<bool> AuthenticateAsync();

    /// <summary>
    /// Logs out the current user and clears cached credentials.
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Lists files in the specified folder or the root application folder.
    /// </summary>
    /// <param name="folderId">The folder ID to list files from, or null for the root folder.</param>
    /// <returns>A list of cloud files with metadata.</returns>
    /// <exception cref="InvalidOperationException">Thrown when not authenticated.</exception>
    Task<List<CloudFile>> ListFilesAsync(string? folderId = null);

    /// <summary>
    /// Uploads a local file to cloud storage.
    /// </summary>
    /// <param name="localPath">The full path to the local file to upload.</param>
    /// <param name="remotePath">The destination path/name in cloud storage.</param>
    /// <param name="progress">Optional progress reporter for upload percentage (0-100).</param>
    /// <returns>The unique file ID in cloud storage.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the local file doesn't exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when not authenticated or upload fails.</exception>
    Task<string> UploadFileAsync(string localPath, string remotePath, IProgress<double>? progress = null);

    /// <summary>
    /// Downloads a file from cloud storage to a local path.
    /// </summary>
    /// <param name="remoteFileId">The unique ID of the file in cloud storage.</param>
    /// <param name="localPath">The destination path for the downloaded file.</param>
    /// <param name="progress">Optional progress reporter for download percentage (0-100).</param>
    /// <exception cref="InvalidOperationException">Thrown when not authenticated or download fails.</exception>
    Task DownloadFileAsync(string remoteFileId, string localPath, IProgress<double>? progress = null);

    /// <summary>
    /// Deletes a file from cloud storage.
    /// </summary>
    /// <param name="fileId">The unique ID of the file to delete.</param>
    /// <exception cref="InvalidOperationException">Thrown when not authenticated or deletion fails.</exception>
    Task DeleteFileAsync(string fileId);
}

/// <summary>
/// Represents metadata for a file in cloud storage.
/// </summary>
/// <param name="Id">The unique file identifier.</param>
/// <param name="Name">The file name.</param>
/// <param name="MimeType">The MIME type of the file.</param>
/// <param name="Size">The file size in bytes, if available.</param>
/// <param name="ModifiedTime">The last modification timestamp, if available.</param>
public record CloudFile(
    string Id,
    string Name,
    string MimeType,
    long? Size,
    DateTime? ModifiedTime
);
```

**Why:** Improves developer experience and makes the API self-documenting.

---

## 7. Low Priority: Move Sentry DSN to Configuration

### Current Code (Plugin.cs)
```csharp
public Plugin()
{
    _sentryGuard = SentrySdk.Init(o =>
    {
        o.Dsn = "https://a4bff996c43a4087136bf25866d17ffc@o4510663508754432.ingest.us.sentry.io/4510663528611840";
        o.Release = "ymm4-cloudsync@1.0.0"; 
        o.SendDefaultPii = false; 
    });

    CheckFileAssociation();
    Task.Run(CleanUpTempFiles);
}
```

### Recommended Code

Create `appsettings.json`:
```json
{
  "Sentry": {
    "Dsn": "https://a4bff996c43a4087136bf25866d17ffc@o4510663508754432.ingest.us.sentry.io/4510663528611840",
    "Release": "ymm4-cloudsync@1.0.0",
    "SendDefaultPii": false
  }
}
```

Update `Plugin.cs`:
```csharp
private static SentrySettings LoadSentrySettings()
{
    try
    {
        var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        var configPath = Path.Combine(pluginDir, "appsettings.json");
        
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<AppSettings>(json);
            return config?.Sentry ?? new SentrySettings();
        }
    }
    catch
    {
        // Fall back to defaults
    }
    
    return new SentrySettings();
}

public Plugin()
{
    var sentrySettings = LoadSentrySettings();
    
    _sentryGuard = SentrySdk.Init(o =>
    {
        o.Dsn = sentrySettings.Dsn;
        o.Release = sentrySettings.Release;
        o.SendDefaultPii = sentrySettings.SendDefaultPii;
    });

    CheckFileAssociation();
    Task.Run(CleanUpTempFiles);
}

private class AppSettings
{
    public SentrySettings Sentry { get; set; } = new();
}

private class SentrySettings
{
    public string Dsn { get; set; } = "";
    public string Release { get; set; } = "ymm4-cloudsync@1.0.0";
    public bool SendDefaultPii { get; set; } = false;
}
```

**Why:** Makes configuration more flexible and easier to update without recompiling.

---

## 8. Bonus: Add Structured Logging

Consider adding a structured logging framework like Serilog for better debugging:

```csharp
public static class AppLogger
{
    private static readonly ILogger Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.File(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YMM4CloudSync", "logs", "log-.txt"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7)
        .CreateLogger();

    public static void Debug(string message) => Logger.Debug(message);
    public static void Info(string message) => Logger.Information(message);
    public static void Warning(string message, Exception? ex = null) => Logger.Warning(ex, message);
    public static void Error(string message, Exception ex) => Logger.Error(ex, message);
}
```

Then replace `Debug.WriteLine` calls with `AppLogger` calls throughout the codebase.

---

## Implementation Priority

1. **Week 1:** Critical items (#1, #2, #3)
2. **Week 2:** High priority items (#4)
3. **Week 3:** Medium priority items (#5, #6)
4. **Week 4:** Low priority items (#7, #8)

---

## Testing Recommendations

After implementing these changes:

1. **Unit Tests:** Test each improved component in isolation
2. **Integration Tests:** Verify cloud service operations still work correctly
3. **Regression Tests:** Ensure existing functionality isn't broken
4. **Memory Leak Tests:** Use a profiler to verify no resource leaks
5. **Exception Tests:** Verify all exceptions are properly handled and logged

---

*Document created: 2026-01-07*  
*Review version: 1.0*
