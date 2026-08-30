using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dropbox.Api;
using Dropbox.Api.Files;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Commons.Security;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Core.Services;

public class DropboxService : ICloudStorageService, IDisposable
{
    public string ServiceName => "Dropbox";

    public string ConnectionKey => "dropbox";

    private const string AppKey = DropboxCredentials.ClientId;

    private static readonly int[] RedirectPorts = [52475, 52476, 52477];

    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(5);

    private const string TokenCachePath = "dropbox_token_cache.bin";

    /// <summary>
    /// Dropbox upload limit for simple upload.
    /// Files larger than 150MB should use chunked upload session.
    /// See: https://www.dropbox.com/developers/documentation/http/documentation#files-upload
    /// </summary>
    private const long UploadLimitBytes = 150 * 1024 * 1024; // 150MB

    /// <summary>
    /// Chunk size for uploading large files to Dropbox.
    /// Recommended size is between 4MB-150MB for optimal performance.
    /// </summary>
    private const int ChunkSizeBytes = 8 * 1024 * 1024; // 8MB

    private static string GetTokenPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", TokenCachePath);

    private static string GetRedirectUri(int port) => $"http://127.0.0.1:{port}/authorize";

    private DropboxClient? _client;
    private bool _disposed;

    public bool IsAuthenticated => _client != null;

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tokenPath = GetTokenPath();
            var tokenData = SecureStorageHelper.Load(tokenPath);

            if (tokenData == null || tokenData.Length == 0)
                return false;

            var refreshToken = Encoding.UTF8.GetString(tokenData);

            _client = new DropboxClient(refreshToken, AppKey);

            await _client.Users.GetCurrentAccountAsync();

            return true;
        }
        catch (Exception ex)
        {
            SentryReporter.Capture(ex);
            Debug.WriteLine($"[Dropbox] Silent auth failed: {ex.Message}");
            _client?.Dispose();
            _client = null;
            return false;
        }
    }

    public async Task<bool> AuthenticateInteractiveAsync(CancellationToken cancellationToken = default)
    {
        HttpListener? listener = null;

        try
        {
            listener = StartCallbackListener(out var redirectUri);

            var pkceFlow = new PKCEOAuthFlow();

            var expectedState = GenerateState();

            var authorizeUri = pkceFlow.GetAuthorizeUri(
                OAuthResponseType.Code,
                AppKey,
                redirectUri,
                state: expectedState,
                tokenAccessType: TokenAccessType.Offline
            );

            Process.Start(new ProcessStartInfo(authorizeUri.ToString()) { UseShellExecute = true });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(AuthorizationTimeout);

            var context = await WaitForCallbackAsync(listener, timeoutCts.Token);

            var code = context.Request.QueryString["code"];
            var returnedState = context.Request.QueryString["state"];
            var error = context.Request.QueryString["error"];

            var stateMatches = string.Equals(returnedState, expectedState, StringComparison.Ordinal);
            var succeeded = stateMatches && string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code);

            await WriteCallbackResponseAsync(context, succeeded, stateMatches);

            listener.Stop();

            if (!stateMatches)
                throw new InvalidOperationException(
                    "認証の応答が正しくありません。\n別のアプリケーションからの応答である可能性があります。もう一度やり直してください。");

            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"Dropbox の認証が拒否されました。({error})");

            if (string.IsNullOrEmpty(code)) return false;

            var tokenResult = await pkceFlow.ProcessCodeFlowAsync(code, AppKey, redirectUri);

            if (string.IsNullOrEmpty(tokenResult.RefreshToken))
            {
                throw new InvalidOperationException("リフレッシュトークンが取得できませんでした。連携を解除してやり直してください。");
            }

            var tokenBytes = Encoding.UTF8.GetBytes(tokenResult.RefreshToken);
            SecureStorageHelper.Save(GetTokenPath(), tokenBytes);

            _client = new DropboxClient(tokenResult.RefreshToken, AppKey);

            return true;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[Dropbox] Interactive auth cancelled or timed out.");
            _client?.Dispose();
            _client = null;
            return false;
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
            _client?.Dispose();
            _client = null;
            return false;
        }
        finally
        {
            listener?.Close();
        }
    }

    private static HttpListener StartCallbackListener(out string redirectUri)
    {
        HttpListenerException? lastError = null;

        foreach (var port in RedirectPorts)
        {
            var uri = GetRedirectUri(port);
            var listener = new HttpListener();
            listener.Prefixes.Add(uri + "/");

            try
            {
                listener.Start();
                redirectUri = uri;
                return listener;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
                listener.Close();
            }
        }

        throw new InvalidOperationException(
            $"認証用のポート ({string.Join(", ", RedirectPorts)}) がすべて使用中です。\n" +
            "他のアプリケーションを終了してからもう一度お試しください。", lastError);
    }

    private static async Task<HttpListenerContext> WaitForCallbackAsync(
        HttpListener listener, CancellationToken cancellationToken)
    {
        var contextTask = listener.GetContextAsync();

        var cancellationSignal = new TaskCompletionSource();
        await using var registration = cancellationToken.Register(() => cancellationSignal.TrySetResult());

        if (await Task.WhenAny(contextTask, cancellationSignal.Task) != contextTask)
        {
            ObserveFailure(contextTask);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await contextTask;
    }

    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task WriteCallbackResponseAsync(
        HttpListenerContext context, bool succeeded, bool stateMatches)
    {
        var body = succeeded
            ? "<html><body><h2>Authentication Successful</h2><p>You can close this window now.</p></body></html>"
            : stateMatches
                ? "<html><body><h2>Authentication Failed</h2><p>Please return to YMM4 and try again.</p></body></html>"
                : "<html><body><h2>Authentication Rejected</h2><p>The response did not match this request.</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;

        try
        {
            await context.Response.OutputStream.WriteAsync(buffer);
            await context.Response.OutputStream.FlushAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dropbox] Failed to write callback response: {ex.Message}");
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }

    private static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_client != null)
            {
                await _client.Auth.TokenRevokeAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dropbox] Logout failed: {ex.Message}");
        }
        finally
        {
            _client?.Dispose();
            _client = null;
            SecureStorageHelper.Delete(GetTokenPath());
        }
    }

    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        var path = NormalizePathForListFolder(folderId);

        try
        {
            var list = await RetryHelper.ExecuteWithRetryAsync(
                () => client.Files.ListFolderAsync(path, recursive: false, includeDeleted: false),
                cancellationToken: cancellationToken);

            var result = new List<CloudFile>();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                result.AddRange(list.Entries.Select(item => new CloudFile(
                    item.PathDisplay,
                    item.Name,
                    item.IsFolder ? CloudMimeTypes.DropboxFolder : "application/octet-stream",
                    item.IsFile ? (long?)item.AsFile.Size : null,
                    item.IsFile ? (DateTime?)item.AsFile.ClientModified.ToLocalTime() : null,
                    GetParentPath(item.PathDisplay))));

                if (!list.HasMore) break;

                var cursor = list.Cursor;
                list = await RetryHelper.ExecuteWithRetryAsync(
                    () => client.Files.ListFolderContinueAsync(cursor),
                    cancellationToken: cancellationToken);
            }

            return result
                .OrderByDescending(f => f.IsFolder)
                .ThenByDescending(f => f.ModifiedTime ?? DateTime.MinValue)
                .ToList();
        }
        catch (ApiException<ListFolderError> ex) when (ex.ErrorResponse.IsPath && ex.ErrorResponse.AsPath.Value.IsNotFound)
        {
            return [];
        }
    }

    private async Task<string> UploadToPathAsync(string localPath, string remotePath,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var client = EnsureAuthenticated();

        if (!File.Exists(localPath))
            throw new FileNotFoundException("ファイルが見つかりません。", localPath);

        var uploadPath = NormalizePathForApi(remotePath);

        var fileInfo = new FileInfo(localPath);

        try
        {
            return fileInfo.Length >= UploadLimitBytes
                ? await UploadLargeFileAsync(client, localPath, uploadPath, fileInfo.Length, progress, cancellationToken)
                : await UploadSmallFileAsync(client, localPath, uploadPath, fileInfo.Length, progress, cancellationToken);
        }
        catch (Exception ex) when (CloudErrors.IsStorageQuotaExceeded(ex))
        {
            throw new CloudStorageFullException(CloudErrors.StorageQuotaMessage(ServiceName));
        }
    }

    public Task<string> UploadFileToFolderAsync(string localPath, string? parentFolderId, string fileName,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => UploadToPathAsync(localPath, CombinePath(parentFolderId, fileName), progress, cancellationToken);

    public async Task<CloudFile> CreateFolderAsync(string? parentId, string name,
        CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        var path = NormalizePathForApi(CombinePath(parentId, name));

        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await client.Files.CreateFolderV2Async(path);

                return ToCloudFile(result.Metadata.PathDisplay ?? path, result.Metadata.Name);
            }
            catch (ApiException<CreateFolderError> ex) when (ex.ErrorResponse.IsPath
                                                             && ex.ErrorResponse.AsPath.Value.IsConflict)
            {
                return ToCloudFile(path, name);
            }
        }, cancellationToken: cancellationToken);
    }

    private static CloudFile ToCloudFile(string path, string name)
        => new(path, name, CloudMimeTypes.DropboxFolder, null, null, GetParentPath(path));

    private static string CombinePath(string? parentPath, string name)
    {
        var trimmedName = name.Replace('\\', '/').Trim('/');

        return string.IsNullOrEmpty(parentPath) ? "/" + trimmedName : parentPath.TrimEnd('/') + "/" + trimmedName;
    }

    private static async Task<string> UploadSmallFileAsync(DropboxClient client, string localPath, string remotePath, long totalSize,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var metadata = await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            progress?.Report(0);

            return await client.Files.UploadAsync(
                remotePath,
                WriteMode.Overwrite.Instance,
                body: stream);
        }, cancellationToken: cancellationToken);

        progress?.Report(100.0);
        _ = totalSize;

        return metadata.PathDisplay ?? remotePath;
    }

    private static async Task<string> UploadLargeFileAsync(DropboxClient client, string localPath, string remotePath, long totalSize,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var chunk = new byte[ChunkSizeBytes];

        cancellationToken.ThrowIfCancellationRequested();

        var read = await stream.ReadAsync(chunk.AsMemory(0, ChunkSizeBytes), cancellationToken);

        var sessionStartResult = await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var mem = new MemoryStream(chunk, 0, read);
            return await client.Files.UploadSessionStartAsync(body: mem);
        }, cancellationToken: cancellationToken);

        var sessionId = sessionStartResult.SessionId;
        long uploaded = read;
        progress?.Report(totalSize > 0 ? (double)uploaded / totalSize * 100 : 0);

        try
        {
            if (uploaded >= totalSize)
            {
                return await FinishSessionAsync(client, sessionId, uploaded, remotePath,
                    Array.Empty<byte>(), 0, progress, cancellationToken);
            }

            while (uploaded < totalSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytesToRead = (int)Math.Min(ChunkSizeBytes, totalSize - uploaded);
                var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, bytesToRead), cancellationToken);

                if (bytesRead == 0) break;

                var isFinalChunk = uploaded + bytesRead >= totalSize;

                if (isFinalChunk)
                {
                    return await FinishSessionAsync(client, sessionId, uploaded, remotePath,
                        chunk, bytesRead, progress, cancellationToken);
                }

                var offset = (ulong)uploaded;
                await RetryHelper.ExecuteWithRetryAsync(async () =>
                {
                    using var mem = new MemoryStream(chunk, 0, bytesRead);
                    var cursor = new UploadSessionCursor(sessionId, offset);
                    await client.Files.UploadSessionAppendV2Async(cursor, body: mem);
                }, cancellationToken: cancellationToken);

                uploaded += bytesRead;
                progress?.Report((double)uploaded / totalSize * 100);
            }

            return await FinishSessionAsync(client, sessionId, uploaded, remotePath,
                Array.Empty<byte>(), 0, progress, cancellationToken);
        }
        catch
        {
            await CloseSessionQuietlyAsync(client, sessionId, uploaded);
            throw;
        }
    }

    private static async Task<string> FinishSessionAsync(DropboxClient client, string sessionId, long offset, string remotePath,
        byte[] buffer, int count, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var metadata = await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var mem = new MemoryStream(buffer, 0, count);
            var cursor = new UploadSessionCursor(sessionId, (ulong)offset);
            var commitInfo = new CommitInfo(remotePath, WriteMode.Overwrite.Instance);
            return await client.Files.UploadSessionFinishAsync(cursor, commitInfo, body: mem);
        }, cancellationToken: cancellationToken);

        progress?.Report(100.0);
        return metadata.PathDisplay ?? remotePath;
    }

    private static async Task CloseSessionQuietlyAsync(DropboxClient client, string sessionId, long offset)
    {
        try
        {
            using var empty = new MemoryStream([], 0, 0);
            var cursor = new UploadSessionCursor(sessionId, (ulong)offset);
            await client.Files.UploadSessionAppendV2Async(cursor, close: true, body: empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dropbox] Failed to close upload session: {ex.Message}");
        }
    }

    public async Task DownloadFileAsync(string remoteFileId, string localPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();

        var downloadPath = NormalizePathForApi(remoteFileId);

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{localPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                using var response = await client.Files.DownloadAsync(downloadPath);
                var totalSize = (long)response.Response.Size;

                await using var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var srcStream = await response.GetContentAsStreamAsync();

                var buffer = new byte[64 * 1024];
                long totalRead = 0;
                int read;

                while ((read = await srcStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    if (totalSize > 0)
                    {
                        progress?.Report((double)totalRead / totalSize * 100.0);
                    }
                }
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
            Debug.WriteLine($"[Dropbox] Failed to delete temporary file: {ex.Message}");
        }
    }

    public async Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var client = EnsureAuthenticated();
        await RetryHelper.ExecuteWithRetryAsync(
            () => client.Files.DeleteV2Async(NormalizePathForApi(fileId)),
            cancellationToken: cancellationToken);
    }

    private DropboxClient EnsureAuthenticated()
    {
        return _client
               ?? throw new CloudNotAuthenticatedException("Dropboxに認証されていません。連携タブからサインインしてください。");
    }

    private static string? GetParentPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var lastSlash = normalized.LastIndexOf('/');

        if (lastSlash < 0) return null;

        var parent = normalized[..lastSlash];

        return parent.Length == 0 ? "/" : parent;
    }

    private static string NormalizePathForListFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        var normalized = NormalizePathCore(path);
        return normalized == "/" ? "" : normalized;
    }

    private static string NormalizePathForApi(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("ファイルパスを空にすることはできません。", nameof(path));
        }
        return NormalizePathCore(path);
    }

    private static string NormalizePathCore(string path)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var finalSegments = new LinkedList<string>();

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                {
                    if (finalSegments.Count > 0)
                    {
                        finalSegments.RemoveLast();
                    }

                    break;
                }
                default:
                    finalSegments.AddLast(segment);
                    break;
            }
        }

        return "/" + string.Join("/", finalSegments);
    }

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
            _client?.Dispose();
        }
        _disposed = true;
    }
}
