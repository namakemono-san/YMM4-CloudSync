using Dropbox.Api;
using Dropbox.Api.Files;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Commons.Security;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Core.Services;

public class DropboxService : ICloudStorageService, IDisposable
{
    public string ServiceName => "Dropbox";

    private const string AppKey = DropboxCredentials.ClientId;
    private const string AppSecret = DropboxCredentials.ClientSecret; 
    
    private const string RedirectUri = "http://127.0.0.1:52475/authorize";
    
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

    private DropboxClient? _client;
    private bool _disposed;

    public bool IsAuthenticated => _client != null;

    public async Task<bool> AuthenticateAsync()
    {
        try
        {
            var tokenPath = GetTokenPath();
            var tokenData = SecureStorageHelper.Load(tokenPath);

            if (tokenData == null || tokenData.Length == 0)
                return false;

            var refreshToken = Encoding.UTF8.GetString(tokenData);
            
            _client = new DropboxClient(refreshToken, AppKey, AppSecret);

            await _client.Users.GetCurrentAccountAsync();

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dropbox] Silent auth failed: {ex.Message}");
            _client?.Dispose();
            _client = null;
            return false;
        }
    }

    public async Task<bool> AuthenticateInteractiveAsync()
    {
        try
        {
            var pkceFlow = new PKCEOAuthFlow();
            var authorizeUri = pkceFlow.GetAuthorizeUri(
                OAuthResponseType.Code, 
                AppKey, 
                RedirectUri,
                state: null,
                tokenAccessType: TokenAccessType.Offline
            );

            using var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri + "/");
            listener.Start();

            Process.Start(new ProcessStartInfo(authorizeUri.ToString()) { UseShellExecute = true });

            var context = await listener.GetContextAsync();
            var code = context.Request.QueryString["code"];

            const string responseString = "<html><body><h2>Authentication Successful</h2><p>You can close this window now.</p></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseString);
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            await Task.Delay(500);
            context.Response.OutputStream.Close();
            listener.Stop();

            if (string.IsNullOrEmpty(code)) return false;

            var tokenResult = await pkceFlow.ProcessCodeFlowAsync(code, AppKey, RedirectUri);

            if (string.IsNullOrEmpty(tokenResult.RefreshToken))
            {
                throw new Exception("リフレッシュトークンが取得できませんでした。連携を解除してやり直してください。");
            }

            var tokenBytes = Encoding.UTF8.GetBytes(tokenResult.RefreshToken);
            SecureStorageHelper.Save(GetTokenPath(), tokenBytes);

            _client = new DropboxClient(tokenResult.RefreshToken, AppKey, AppSecret);

            return true;
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
            _client?.Dispose();
            _client = null;
            return false;
        }
    }

    public async Task LogoutAsync()
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


    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null)
    {
        EnsureAuthenticated();
        
        var path = NormalizePathForListFolder(folderId);

        try
        {
            var list = await _client!.Files.ListFolderAsync(path, recursive: false, includeDeleted: false);
            var result = new List<CloudFile>();

            while (true)
            {
                result.AddRange(list.Entries.Select(item => new CloudFile(item.PathDisplay, item.Name, item.IsFolder ? "application/vnd.dropbox.folder" : "application/octet-stream", item.IsFile ? (long?)item.AsFile.Size : null, item.IsFile ? (DateTime?)item.AsFile.ClientModified.ToLocalTime() : null)));

                if (!list.HasMore) break;
                list = await _client.Files.ListFolderContinueAsync(list.Cursor);
            }
            return result.OrderByDescending(f => f.ModifiedTime).ToList();
        }
        catch (ApiException<ListFolderError> ex) when (ex.ErrorResponse.IsPath && ex.ErrorResponse.AsPath.Value.IsNotFound)
        {
            return [];
        }
    }

    public async Task<string> UploadFileAsync(string localPath, string remotePath, IProgress<double>? progress = null)
    {
        EnsureAuthenticated();

        if (!File.Exists(localPath))
            throw new FileNotFoundException("ファイルが見つかりません。", localPath);

        var uploadPath = NormalizePathForApi(remotePath);
        
        var fileInfo = new FileInfo(localPath);

        if (fileInfo.Length > UploadLimitBytes)
        {
            return await UploadLargeFileAsync(localPath, uploadPath, fileInfo.Length, progress);
        }

        return await UploadLargeFileAsync(localPath, uploadPath, fileInfo.Length, progress);
    }

    private async Task<string> UploadLargeFileAsync(string localPath, string remotePath, long totalSize, IProgress<double>? progress)
    {
        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            
            var chunk = new byte[ChunkSizeBytes];
            var read = await stream.ReadAsync(chunk.AsMemory(0, ChunkSizeBytes));
            
            UploadSessionStartResult sessionStartResult;
            using (var mem = new MemoryStream(chunk, 0, read))
            {
                sessionStartResult = await _client!.Files.UploadSessionStartAsync(body: mem);
            }

            long uploaded = read;
            progress?.Report((double)uploaded / totalSize * 100);

            while (uploaded < totalSize)
            {
                var bytesToRead = (int)Math.Min(ChunkSizeBytes, totalSize - uploaded);
                var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, bytesToRead));
                
                if (bytesRead == 0) break;

                using (var mem = new MemoryStream(chunk, 0, bytesRead))
                {
                    if (uploaded + bytesRead < totalSize)
                    {
                        var cursor = new UploadSessionCursor(sessionStartResult.SessionId, (ulong)uploaded);
                        await _client.Files.UploadSessionAppendV2Async(cursor, body: mem);
                    }
                    else
                    {
                        var cursor = new UploadSessionCursor(sessionStartResult.SessionId, (ulong)uploaded);
                        var commitInfo = new CommitInfo(remotePath, WriteMode.Overwrite.Instance);
                        var metadata = await _client.Files.UploadSessionFinishAsync(cursor, commitInfo, body: mem);
                        progress?.Report(100.0);
                        return metadata.Id;
                    }
                }
                
                uploaded += bytesRead;
                progress?.Report((double)uploaded / totalSize * 100);
            }
            
            throw new InvalidOperationException("大容量ファイルのアップロードに失敗しました。");
        });
    }

    public async Task DownloadFileAsync(string remoteFileId, string localPath, IProgress<double>? progress = null)
    {
        EnsureAuthenticated();
        
        var downloadPath = NormalizePathForApi(remoteFileId);

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = localPath + ".tmp";

        try
        {
            {
                using var response = await _client!.Files.DownloadAsync(downloadPath);
                var totalSize = (long)response.Response.Size;
                
                await using var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var srcStream = await response.GetContentAsStreamAsync();
                
                var buffer = new byte[64 * 1024];
                long totalRead = 0;
                int read;
                
                while ((read = await srcStream.ReadAsync(buffer)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;
                    if (totalSize > 0)
                    {
                        progress?.Report((double)totalRead / totalSize * 100.0);
                    }
                }
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
            catch (Exception ex)
            { 
                Debug.WriteLine($"[Dropbox] Failed to delete temporary file: {ex.Message}");
            }
            throw;
        }
    }

    public async Task DeleteFileAsync(string fileId)
    {
        EnsureAuthenticated();
        await _client!.Files.DeleteV2Async(NormalizePathForApi(fileId));
    }

    private void EnsureAuthenticated()
    {
        if (_client == null)
            throw new InvalidOperationException("Dropboxに認証されていません。連携タブからサインインしてください。");
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