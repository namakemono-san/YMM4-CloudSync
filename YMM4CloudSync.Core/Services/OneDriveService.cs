using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Commons.Security;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Core.Services;

public sealed class OneDriveService : ICloudStorageService, IDisposable
{
    public string ServiceName => "OneDrive";

    public string ConnectionKey => "onedrive";
    public bool IsAuthenticated => _pca != null && _account != null;

    private static readonly string[] Scopes = ["Files.ReadWrite.AppFolder"];
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private static readonly string TokenCachePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YMM4CloudSync", "onedrive_msal_cache.bin");
    
    /// <summary>
    /// OneDrive recommends chunk sizes above this threshold for optimal upload.
    /// Files smaller than 4MB can be uploaded in a single PUT request.
    /// See: https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession
    /// </summary>
    private const long ChunkThresholdBytes = 4 * 1024 * 1024; // 4MB
    
    // Using Lock class (.NET 9+) for thread-safe token cache access
    // This ensures proper synchronization when multiple operations access the cache
    private static readonly Lock FileLock = new();
    
    // Static HttpClient to avoid socket exhaustion issues
    // See: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        return new HttpClient(handler);
    }
    
    private IPublicClientApplication? _pca;
    private IAccount? _account;
    private bool _disposed;
    private string? _appRootId;

    public void Dispose()
    {
        if (_disposed) return;

        _account = null;
        _appRootId = null;
        _pca = null;
        _disposed = true;
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureClient();

            var accounts = await _pca!.GetAccountsAsync();
            _account = accounts.FirstOrDefault();

            var silent = await _pca.AcquireTokenSilent(Scopes, _account).ExecuteAsync(cancellationToken);
            _account = silent.Account;

            return true;
        }
        catch
        {
            _account = null;
            _appRootId = null;
            return false;
        }
    }

    public async Task<bool> AuthenticateInteractiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureClient();

            var interactive = await _pca!.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken);

            _account = interactive.Account;
            return true;
        }
        catch (OperationCanceledException)
        {
            _account = null;
            _appRootId = null;
            return false;
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
            _account = null;
            _appRootId = null;
            return false;
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (_pca != null)
        {
            var accounts = await _pca.GetAccountsAsync();
            foreach (var a in accounts)
                await _pca.RemoveAsync(a);
        }

        _account = null;
        _appRootId = null;

        SecureStorageHelper.Delete(TokenCachePath);
    }

    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var target = folderId ?? _appRootId;

        var url = target is null
            ? $"{GraphBase}/me/drive/special/approot/children?$orderby=lastModifiedDateTime desc"
            : $"{GraphBase}/me/drive/items/{Uri.EscapeDataString(target)}/children?$orderby=lastModifiedDateTime desc";

        var list = new List<CloudFile>();

        string? nextUrl = url;
        var isFirstPage = true;

        while (nextUrl != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageUrl = nextUrl;
            var allowMissing = isFirstPage;
            isFirstPage = false;

            var page = await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                using var resp = await SendAsync(HttpMethod.Get, pageUrl, null,
                    cancellationToken: cancellationToken);

                if (allowMissing && resp.StatusCode == HttpStatusCode.NotFound)
                {
                    return (Items: new List<CloudFile>(), NextLink: (string?)null);
                }

                await EnsureSuccessOrThrowAsync(resp);

                await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);

                var items = new List<CloudFile>();

                foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString() ?? "";
                    var name = item.GetProperty("name").GetString() ?? "";
                    var size = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : (long?)null;
                    var modified = item.TryGetProperty("lastModifiedDateTime", out var lm) ? lm.GetDateTime() : (DateTime?)null;
                    var isFolder = item.TryGetProperty("folder", out _);
                    var parentId = item.TryGetProperty("parentReference", out var parentRef)
                                   && parentRef.TryGetProperty("id", out var parentIdValue)
                        ? parentIdValue.GetString()
                        : target;

                    if (folderId is null && _appRootId is null && !string.IsNullOrEmpty(parentId))
                        _appRootId = parentId;

                    items.Add(new CloudFile(
                        id,
                        name,
                        isFolder ? CloudMimeTypes.OneDriveFolder : "application/octet-stream",
                        size,
                        modified,
                        parentId));
                }

                var nextLink = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                    ? next.GetString()
                    : null;

                return (Items: items, NextLink: nextLink);
            }, cancellationToken: cancellationToken);

            list.AddRange(page.Items);
            nextUrl = page.NextLink;
        }

        return list;
    }

    public async Task<AssetRootListing?> TryOpenAssetRootAsync(string name,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var url = $"{GraphBase}/me/drive/special/approot:/{EscapePath(name)}?$expand=children";

        using var resp = await SendAsync(HttpMethod.Get, url, null, cancellationToken: cancellationToken);

        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;

        if (!root.TryGetProperty("id", out var idValue)) return null;

        var folderId = idValue.GetString();

        if (string.IsNullOrEmpty(folderId)) return null;

        if (root.TryGetProperty("parentReference", out var parentRef)
            && parentRef.TryGetProperty("id", out var appRoot)
            && appRoot.GetString() is { Length: > 0 } appRootId)
        {
            _appRootId = appRootId;
        }

        var files = new List<CloudFile>();

        if (root.TryGetProperty("children", out var children))
        {
            files.AddRange(children.EnumerateArray().Select(item => ToCloudFile(item, folderId)));
        }

        if (root.TryGetProperty("children@odata.nextLink", out _))
        {
            return new AssetRootListing(folderId, await ListFilesAsync(folderId, cancellationToken));
        }

        return new AssetRootListing(folderId, [.. files
            .OrderByDescending(f => f.ModifiedTime ?? DateTime.MinValue)]);
    }

    private static CloudFile ToCloudFile(JsonElement item, string? fallbackParentId)
    {
        var parentId = item.TryGetProperty("parentReference", out var parentRef)
                       && parentRef.TryGetProperty("id", out var parentIdValue)
            ? parentIdValue.GetString()
            : fallbackParentId;

        return new CloudFile(
            item.GetProperty("id").GetString() ?? "",
            item.GetProperty("name").GetString() ?? "",
            item.TryGetProperty("folder", out _) ? CloudMimeTypes.OneDriveFolder : "application/octet-stream",
            item.TryGetProperty("size", out var size) ? size.GetInt64() : null,
            item.TryGetProperty("lastModifiedDateTime", out var modified) ? modified.GetDateTime() : null,
            parentId);
    }

    public Task<string> UploadFileToFolderAsync(string localPath, string? parentFolderId, string fileName,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => UploadCoreAsync(localPath, BuildItemTarget(parentFolderId, fileName), progress, cancellationToken);

    private static string BuildItemTarget(string? parentFolderId, string relativePath)
        => string.IsNullOrEmpty(parentFolderId)
            ? $"special/approot:/{EscapePath(relativePath)}:"
            : $"items/{Uri.EscapeDataString(parentFolderId)}:/{EscapePath(relativePath)}:";

    private async Task<string> UploadCoreAsync(string localPath, string itemTarget,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();

        if (!File.Exists(localPath))
            throw new FileNotFoundException("ファイルが見つかりません。", localPath);

        var fileInfo = new FileInfo(localPath);

        if (fileInfo.Length > ChunkThresholdBytes)
        {
            return await UploadLargeFileAsync(localPath, itemTarget, progress, cancellationToken);
        }

        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var url = $"{GraphBase}/me/drive/{itemTarget}/content";

            using var content = new ProgressStreamContent(fs, fs.Length, progress);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var resp = await SendAsync(HttpMethod.Put, url, content,
                cancellationToken: cancellationToken);
            await EnsureSuccessOrThrowAsync(resp);

            await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);

            return doc.RootElement.GetProperty("id").GetString() ?? "";
        }, cancellationToken: cancellationToken);
    }

    private async Task<string> UploadLargeFileAsync(string localPath, string itemTarget,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var sessionUrl = await CreateUploadSessionAsync(itemTarget, cancellationToken);

        try
        {
            await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var totalSize = fs.Length;
            // OneDrive recommends chunk sizes that are multiples of 320 KiB (327,680 bytes)
            // Using 3.2MB (10 * 320KB) for optimal upload performance
            // See: https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession
            const int oneDriveRecommendedChunkUnit = 320 * 1024;
            const int chunkMultiplier = 10;
            const int chunkSize = chunkMultiplier * oneDriveRecommendedChunkUnit;

            var buffer = new byte[chunkSize];
            long uploaded = 0;
            string? fileId = null;

            while (uploaded < totalSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytesToRead = (int)Math.Min(chunkSize, totalSize - uploaded);
                var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);

                if (bytesRead == 0) break;

                var rangeStart = uploaded;
                var rangeEnd = uploaded + bytesRead - 1;

                fileId = await RetryHelper.ExecuteWithRetryAsync(async () =>
                {
                    using var content = new ByteArrayContent(buffer, 0, bytesRead);
                    content.Headers.Add("Content-Range", $"bytes {rangeStart}-{rangeEnd}/{totalSize}");
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    using var req = new HttpRequestMessage(HttpMethod.Put, sessionUrl);
                    req.Content = content;
                    using var resp = await SharedHttpClient.SendAsync(req, cancellationToken);

                    if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Accepted)
                    {
                        await EnsureSuccessOrThrowAsync(resp);
                    }

                    await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);

                    return doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                }, cancellationToken: cancellationToken);

                uploaded += bytesRead;
                progress?.Report(uploaded * 100.0 / totalSize);
            }

            return fileId ?? throw new InvalidOperationException("アップロードが完了しましたが、ファイルIDを取得できませんでした。");
        }
        catch
        {
            await CancelUploadSessionQuietlyAsync(sessionUrl);
            throw;
        }
    }

    private async Task<string> CreateUploadSessionAsync(string itemTarget, CancellationToken cancellationToken)
    {
        var url = $"{GraphBase}/me/drive/{itemTarget}/createUploadSession";

        var json = JsonSerializer.Serialize(new { item = new Dictionary<string, object>() });

        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await SendAsync(HttpMethod.Post, url, content,
                cancellationToken: cancellationToken);
            await EnsureSuccessOrThrowAsync(resp);

            await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);

            return doc.RootElement.GetProperty("uploadUrl").GetString()
                   ?? throw new InvalidOperationException("アップロードURLを取得できませんでした。");
        }, cancellationToken: cancellationToken);
    }

    private static async Task CancelUploadSessionQuietlyAsync(string sessionUrl)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, sessionUrl);
            using var resp = await SharedHttpClient.SendAsync(req);

            if (!resp.IsSuccessStatusCode)
                Debug.WriteLine($"[OneDrive] Upload session cancel returned HTTP {(int)resp.StatusCode}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OneDrive] Failed to cancel upload session: {ex.Message}");
        }
    }

    public async Task DownloadFileAsync(string remoteFileId, string localPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var url = $"{GraphBase}/me/drive/items/{remoteFileId}/content";

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{localPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var resp = await SendAsync(HttpMethod.Get, url, null,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await EnsureSuccessOrThrowAsync(resp);

                var total = resp.Content.Headers.ContentLength ?? 0;

                await using var input = await resp.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

                await CopyWithProgressAsync(input, output, total, progress, cancellationToken);
            }, cancellationToken: cancellationToken);

            File.Move(tempPath, localPath, overwrite: true);
        }
        catch
        {
            DeleteTempFileQuietly(tempPath);
            throw;
        }
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
            Debug.WriteLine($"[OneDrive] Failed to delete temporary file: {ex.Message}");
        }
    }

    public async Task<CloudFile> CreateFolderAsync(string? parentId, string name,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var parent = parentId;

        if (string.IsNullOrEmpty(parent))
        {
            parent = await EnsureAppRootIdAsync(cancellationToken)
                     ?? throw new InvalidOperationException(
                         "OneDrive のアプリフォルダーを準備できませんでした。\n" +
                         "連携を解除して再連携するか、時間をおいて再試行してください。");
        }

        var url = $"{GraphBase}/me/drive/items/{Uri.EscapeDataString(parent)}/children";

        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["folder"] = new Dictionary<string, object>(),
            ["@microsoft.graph.conflictBehavior"] = "fail"
        });

        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await SendAsync(HttpMethod.Post, url, content,
                cancellationToken: cancellationToken);

            if (resp.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await ListFilesAsync(parent, cancellationToken);

                var match = existing.FirstOrDefault(f =>
                    f.IsFolder && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                if (match != null) return match;
            }

            await EnsureSuccessOrThrowAsync(resp);

            await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);

            var root = doc.RootElement;

            return new CloudFile(
                root.GetProperty("id").GetString() ?? "",
                root.GetProperty("name").GetString() ?? name,
                CloudMimeTypes.OneDriveFolder,
                null,
                root.TryGetProperty("lastModifiedDateTime", out var lm) ? lm.GetDateTime() : null,
                parent);
        }, cancellationToken: cancellationToken);
    }

    private async Task<string?> EnsureAppRootIdAsync(CancellationToken cancellationToken)
    {
        var id = await TryGetAppRootIdAsync(cancellationToken);

        if (id != null) return id;

        await MaterializeAppRootAsync(cancellationToken);

        return await TryGetAppRootIdAsync(cancellationToken);
    }

    private async Task<string?> TryGetAppRootIdAsync(CancellationToken cancellationToken)
    {
        if (_appRootId != null) return _appRootId;

        using var resp = await SendAsync(HttpMethod.Get, $"{GraphBase}/me/drive/special/approot", null,
            cancellationToken: cancellationToken);

        if (!resp.IsSuccessStatusCode) return null;

        await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);

        _appRootId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;

        return _appRootId;
    }

    private async Task MaterializeAppRootAsync(CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await SendAsync(HttpMethod.Put,
            $"{GraphBase}/me/drive/special/approot:/.ymm4cloudsync:/content", content,
            cancellationToken: cancellationToken);

        if (!resp.IsSuccessStatusCode) return;

        await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("id", out var idValue)) return;

        var id = idValue.GetString();

        if (string.IsNullOrEmpty(id)) return;

        using var delete = await SendAsync(HttpMethod.Delete,
            $"{GraphBase}/me/drive/items/{Uri.EscapeDataString(id)}", null,
            cancellationToken: cancellationToken);

        Debug.WriteLine($"[OneDrive] Placeholder cleanup: {delete.StatusCode}");
    }

    public async Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var resp = await SendAsync(HttpMethod.Delete, $"{GraphBase}/me/drive/items/{fileId}", null,
                cancellationToken: cancellationToken);
            await EnsureSuccessOrThrowAsync(resp);
        }, cancellationToken: cancellationToken);
    }

    private void EnsureClient()
    {
        if (_pca != null) return;

        _pca = PublicClientApplicationBuilder
            .Create(OneDriveCredentials.ClientId)
            .WithAuthority("https://login.microsoftonline.com/consumers")
            .WithRedirectUri("http://localhost")
            .Build();

        RegisterTokenCache(_pca.UserTokenCache);
    }

    private void EnsureAuthenticated()
    {
        EnsureClient();
        if (_account == null)
            throw new CloudNotAuthenticatedException("OneDrive に連携されていません。\n連携タブからサインインしてください。");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        EnsureClient();

        if (_account == null)
            throw new CloudNotAuthenticatedException("OneDrive に連携されていません。\n連携タブからサインインしてください。");

        var result = await _pca!.AcquireTokenSilent(Scopes, _account).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content != null) req.Content = content;
        return await SharedHttpClient.SendAsync(req, completion, cancellationToken);
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;

        var code = (int)resp.StatusCode;

        string? graphMessage = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var msg))
                {
                    graphMessage = msg.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            SentryReporter.Capture(ex);
        }

        var message = code switch
        {
            400 => "要求が不正です。\nファイル名やパスに使えない文字が含まれている可能性があります。",
            401 => "認証が切れました。\n連携を解除して再連携してください。",
            403 => "アクセス権がありません。\n許可が必要な可能性があります。",
            404 => "ファイルが見つかりませんでした。\nクラウド側で削除された可能性があります。",
            409 => "競合が発生しました。\n同名ファイルが存在する可能性があります。",
            413 => "ファイルが大きすぎます。",
            429 => "アクセスが集中しています。\n少し待ってから再試行してください。",
            507 => "OneDrive の空き容量が不足しています。\n不要なファイルを削除してごみ箱も空にしてください。",
            >= 500 => "OneDrive 側の問題で操作できません。\n時間をおいて再試行してください。",
            _ => $"OneDrive 操作に失敗しました。(HTTP {code})"
        };

        if (!string.IsNullOrWhiteSpace(graphMessage))
            message = $"{message}\n\n詳細: {graphMessage}";

        throw new InvalidOperationException($"{message}");
    }

    private static async Task CopyWithProgressAsync(Stream input, Stream output, long total,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long done = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;

            if (total > 0)
                progress?.Report(done * 100.0 / total);
        }
    }

    private static string EscapePath(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        return string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    private static void RegisterTokenCache(ITokenCache cache)
    {
        cache.SetBeforeAccess(args =>
        {
            lock (FileLock)
            {
                var data = SecureStorageHelper.Load(TokenCachePath);
                if (data != null) args.TokenCache.DeserializeMsalV3(data);
            }
        });

        cache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (FileLock)
            {
                var data = args.TokenCache.SerializeMsalV3();
                SecureStorageHelper.Save(TokenCachePath, data);
            }
        });
    }
}
