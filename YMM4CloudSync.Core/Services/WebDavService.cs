using System.Diagnostics;
using System.IO;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.Services.WebDav;

namespace YMM4CloudSync.Core.Services;

public sealed class WebDavService : ICloudStorageService, IDisposable
{
    private WebDavClient? _client;
    private string _basePath = "";
    private bool _disposed;

    public WebDavService(WebDavSettings settings)
    {
        Settings = settings;
    }

    public WebDavSettings Settings { get; private set; }

    public string ConnectionId => Settings.Id;

    public string ServiceName => $"WebDAV ({Settings.ResolveDisplayName()})";

    public bool IsAuthenticated => _client != null;

    public static Uri ValidateAndBuildUri(WebDavSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServerUrl))
            throw new InvalidOperationException("サーバー URL を入力してください。");

        if (!Uri.TryCreate(settings.ServerUrl.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("サーバー URL の形式が正しくありません。");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("サーバー URL は http:// または https:// で指定してください。");

        if (uri.Scheme == Uri.UriSchemeHttp && !settings.AllowInsecureConnection)
        {
            throw new InvalidOperationException(
                "http:// の接続は既定で拒否されます。\n\n" +
                "認証情報を実質平文で送信することになるため、https:// のサーバーを使用してください。\n\n" +
                "どうしても必要な場合は「安全でない接続を許可する」を有効にしてください。");
        }

        return uri;
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Connect(Settings);

            await _client!.CheckConnectionAsync(cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebDAV] Silent auth failed: {ex.Message}");
            DisposeClient();
            return false;
        }
    }

    public async Task<bool> ConnectAsync(WebDavSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            Connect(settings);

            await _client!.CheckConnectionAsync(cancellationToken);
            await EnsureBaseDirectoryAsync(cancellationToken);

            Settings = settings;
            WebDavConnectionStore.Upsert(settings);

            return true;
        }
        catch (OperationCanceledException)
        {
            DisposeClient();
            return false;
        }
        catch (Exception ex)
        {
            DisposeClient();
            ErrorReporter.ReportAndShowDialog(ex);
            return false;
        }
    }

    private void Connect(WebDavSettings settings)
    {
        var uri = ValidateAndBuildUri(settings);

        DisposeClient();

        _client = new WebDavClient(uri, settings);
        _basePath = settings.BasePath.Replace('\\', '/').Trim('/');
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        DisposeClient();
        WebDavConnectionStore.Remove(Settings.Id);

        return Task.CompletedTask;
    }

    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        var target = string.IsNullOrEmpty(folderId) ? _basePath : folderId;

        var resources = await RetryHelper.ExecuteWithRetryAsync(
            () => client.ListAsync(target, cancellationToken),
            cancellationToken: cancellationToken);

        return resources
            .Where(r => !string.Equals(r.RelativePath, target, StringComparison.OrdinalIgnoreCase))
            .Select(r => new CloudFile(
                r.RelativePath,
                r.Name,
                r.IsCollection ? CloudMimeTypes.WebDavCollection : "application/octet-stream",
                r.ContentLength,
                r.LastModified))
            .OrderByDescending(f => f.IsFolder)
            .ThenByDescending(f => f.ModifiedTime ?? DateTime.MinValue)
            .ToList();
    }

    public async Task<string> UploadFileAsync(string localPath, string remotePath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        if (!File.Exists(localPath))
            throw new FileNotFoundException("ファイルが見つかりません。", localPath);

        await EnsureBaseDirectoryAsync(cancellationToken);

        var target = CombineWithBasePath(remotePath);
        var length = new FileInfo(localPath).Length;

        await RetryHelper.ExecuteWithRetryAsync(
            () => client.UploadAsync(target, localPath, length, progress, cancellationToken),
            cancellationToken: cancellationToken);

        progress?.Report(100.0);

        return target;
    }

    public async Task DownloadFileAsync(string remoteFileId, string localPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = localPath + ".tmp";

        try
        {
            await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

                await client.DownloadAsync(remoteFileId, destination, progress, cancellationToken);
            }, cancellationToken: cancellationToken);

            File.Move(tempPath, localPath, overwrite: true);
        }
        catch
        {
            DeleteTempFileQuietly(tempPath);
            throw;
        }
    }

    public async Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        await RetryHelper.ExecuteWithRetryAsync(
            () => client.DeleteAsync(fileId, cancellationToken),
            cancellationToken: cancellationToken);
    }

    public async Task RenameFileAsync(string fileId, string newName, CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        var safeName = PathHelper.SanitizeFileName(newName, "project.ymmx");

        var lastSlash = fileId.LastIndexOf('/');
        var parent = lastSlash >= 0 ? fileId[..lastSlash] : "";
        var destination = string.IsNullOrEmpty(parent) ? safeName : $"{parent}/{safeName}";

        await RetryHelper.ExecuteWithRetryAsync(
            () => client.MoveAsync(fileId, destination, false, cancellationToken),
            cancellationToken: cancellationToken);
    }

    private async Task EnsureBaseDirectoryAsync(CancellationToken cancellationToken)
    {
        var client = EnsureAuthenticated();

        if (string.IsNullOrEmpty(_basePath)) return;

        var segments = _basePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";

        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";

            if (await client.ExistsAsync(current, cancellationToken)) continue;

            await client.CreateDirectoryAsync(current, cancellationToken);
        }
    }

    private string CombineWithBasePath(string remotePath)
    {
        var name = remotePath.Replace('\\', '/').Trim('/');

        return string.IsNullOrEmpty(_basePath) ? name : $"{_basePath}/{name}";
    }

    private WebDavClient EnsureAuthenticated()
    {
        return _client
               ?? throw new InvalidOperationException("WebDAV に接続されていません。連携タブから接続設定を行ってください。");
    }

    private static void DeleteTempFileQuietly(string tempPath)
    {
        if (!File.Exists(tempPath)) return;

        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebDAV] Failed to delete temporary file: {ex.Message}");
        }
    }

    private void DisposeClient()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeClient();
    }
}
