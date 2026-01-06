using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YMM4CloudSync.Core.Commons;

namespace YMM4CloudSync.Core.Services;

public sealed class OneDriveService : ICloudStorageService, IDisposable
{
    public string ServiceName => "OneDrive";
    public bool IsAuthenticated => _pca != null && _account != null;

    private static readonly string[] Scopes = ["Files.ReadWrite.AppFolder"];
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private static readonly string TokenCachePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YMM4CloudSync", "onedrive_msal_cache.bin");
    // Using Lock class (.NET 9+) for thread-safe token cache access
    // This ensures proper synchronization when multiple operations access the cache
    private static readonly Lock FileLock = new();
    
    // Static HttpClient to avoid socket exhaustion issues
    // See: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines
    private static readonly HttpClient SharedHttpClient = new();
    
    private IPublicClientApplication? _pca;
    private IAccount? _account;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        // Static HttpClient is managed at application level and should not be disposed here
        _disposed = true;
    }

    public async Task<bool> AuthenticateAsync()
    {
        try
        {
            EnsureClient();

            var accounts = await _pca!.GetAccountsAsync();
            _account = accounts.FirstOrDefault();

            var silent = await _pca.AcquireTokenSilent(Scopes, _account).ExecuteAsync();
            _account = silent.Account;

            return true;
        }
        catch
        {
            _account = null;
            return false;
        }
    }

    public async Task<bool> AuthenticateInteractiveAsync()
    {
        try
        {
            EnsureClient();

            var interactive = await _pca!.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync();

            _account = interactive.Account;
            return true;
        }
        catch
        {
            _account = null;
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        if (_pca != null)
        {
            var accounts = await _pca.GetAccountsAsync();
            foreach (var a in accounts)
                await _pca.RemoveAsync(a);
        }

        _account = null;

        SecureStorageHelper.Delete(TokenCachePath);
    }

    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null)
    {
        EnsureAuthenticated();

        var url = folderId is null
            ? $"{GraphBase}/me/drive/special/approot/children?$orderby=lastModifiedDateTime desc"
            : $"{GraphBase}/me/drive/items/{folderId}/children?$orderby=lastModifiedDateTime desc";

        using var resp = await SendAsync(HttpMethod.Get, url, null);
        await EnsureSuccessOrThrowAsync(resp);

        await using var s = await resp.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(s);

        var list = new List<CloudFile>();

        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var name = item.GetProperty("name").GetString() ?? "";
            var size = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : (long?)null;
            var modified = item.TryGetProperty("lastModifiedDateTime", out var lm) ? lm.GetDateTime() : (DateTime?)null;
            var isFolder = item.TryGetProperty("folder", out _);

            list.Add(new CloudFile(
                id,
                name,
                isFolder ? "application/vnd.microsoft.folder" : "application/octet-stream",
                size,
                modified));
        }

        return list;
    }

    public async Task<string> UploadFileAsync(string localPath, string remotePath, IProgress<double>? progress = null)
    {
        EnsureAuthenticated();

        if (!File.Exists(localPath))
            throw new FileNotFoundException("ファイルが見つかりません。", localPath);

        var fileInfo = new FileInfo(localPath);
        const long chunkThreshold = 4 * 1024 * 1024;

        if (fileInfo.Length > chunkThreshold)
        {
            return await UploadLargeFileAsync(localPath, remotePath, progress);
        }

        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var url = $"{GraphBase}/me/drive/special/approot:/{EscapePath(remotePath)}:/content";

            using var content = new ProgressStreamContent(fs, fs.Length, progress);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var resp = await SendAsync(HttpMethod.Put, url, content);
            await EnsureSuccessOrThrowAsync(resp);

            await using var s = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(s);

            return doc.RootElement.GetProperty("id").GetString() ?? "";
        });
    }

    private async Task<string> UploadLargeFileAsync(string localPath, string remotePath, IProgress<double>? progress)
    {
        var sessionUrl = await CreateUploadSessionAsync(remotePath);
        
        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var totalSize = fs.Length;
        // OneDrive recommends chunk sizes that are multiples of 320 KiB (327,680 bytes)
        // Using 3.2MB (10 * 320KB) for optimal upload performance
        // See: https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession
        const int chunkSize = 10 * 320 * 1024;

        var buffer = new byte[chunkSize];
        long uploaded = 0;
        string? fileId = null;

        while (uploaded < totalSize)
        {
            var bytesToRead = (int)Math.Min(chunkSize, totalSize - uploaded);
            var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, bytesToRead));
            
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
                using var resp = await SharedHttpClient.SendAsync(req);

                if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Accepted)
                {
                    await EnsureSuccessOrThrowAsync(resp);
                }

                await using var s = await resp.Content.ReadAsStreamAsync();
                var doc = await JsonDocument.ParseAsync(s);

                return doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            });

            uploaded += bytesRead;
            progress?.Report(uploaded * 100.0 / totalSize);
        }

        return fileId ?? throw new InvalidOperationException("アップロードが完了しましたが、ファイルIDを取得できませんでした。");
    }

    private async Task<string> CreateUploadSessionAsync(string remotePath)
    {
        var url = $"{GraphBase}/me/drive/special/approot:/{EscapePath(remotePath)}:/createUploadSession";
        
        var body = new { item = new { name = Path.GetFileName(remotePath) } };
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        using var resp = await SendAsync(HttpMethod.Post, url, content);
        await EnsureSuccessOrThrowAsync(resp);
        
        await using var s = await resp.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(s);
        
        return doc.RootElement.GetProperty("uploadUrl").GetString() 
               ?? throw new InvalidOperationException("アップロードURLを取得できませんでした。");
    }

    public async Task DownloadFileAsync(string remoteFileId, string localPath, IProgress<double>? progress = null)
    {
        EnsureAuthenticated();

        var url = $"{GraphBase}/me/drive/items/{remoteFileId}/content";

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = localPath + ".tmp";

        try
        {
            using var resp = await SendAsync(HttpMethod.Get, url, null, HttpCompletionOption.ResponseHeadersRead);
            await EnsureSuccessOrThrowAsync(resp);

            var total = resp.Content.Headers.ContentLength ?? 0;

            await using var input = await resp.Content.ReadAsStreamAsync();
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyWithProgressAsync(input, output, total, progress);
            }

            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
            File.Move(tempPath, localPath);
        }
        catch
        {
            if (!File.Exists(tempPath)) throw;
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // ignored
            }
            throw;
        }
    }

    public async Task DeleteFileAsync(string fileId)
    {
        EnsureAuthenticated();

        using var resp = await SendAsync(HttpMethod.Delete, $"{GraphBase}/me/drive/items/{fileId}", null);
        await EnsureSuccessOrThrowAsync(resp);
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
            throw new InvalidOperationException("OneDrive に連携されていません。\n連携タブからサインインしてください。");
    }

    private async Task<string> GetAccessTokenAsync()
    {
        EnsureClient();

        if (_account == null)
            throw new InvalidOperationException("OneDrive に連携されていません。\n連携タブからサインインしてください。");

        var result = await _pca!.AcquireTokenSilent(Scopes, _account).ExecuteAsync();
        return result.AccessToken;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var token = await GetAccessTokenAsync();
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content != null) req.Content = content;
        return await SharedHttpClient.SendAsync(req, completion);
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
        catch
        {
            // ignored
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

    private static async Task CopyWithProgressAsync(Stream input, Stream output, long total, IProgress<double>? progress)
    {
        var buffer = new byte[64 * 1024];
        long done = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read));
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
