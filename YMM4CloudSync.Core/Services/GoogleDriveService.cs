using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.IO;
using YMM4CloudSync.Core.Commons;

namespace YMM4CloudSync.Core.Services;

public class GoogleDriveService : ICloudStorageService, IDisposable
{
    public string ServiceName => "Google Drive";

    private const string ClientId = GoogleDriveCredentials.ClientId;
    private const string ClientSecret = GoogleDriveCredentials.ClientSecret;
    private const string ApplicationName = "YMM4 Cloud Sync";
    private const string FolderName = "YMM4CloudSync";

    private static readonly string[] Scopes = [DriveService.Scope.DriveFile];
    private static readonly string CredentialPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "google_credentials");

    private DriveService? _driveService;
    private string? _appFolderId;
    private bool _disposed;

    public bool IsAuthenticated => _driveService != null;

    public async Task<bool> AuthenticateAsync()
    {
        if (!Directory.Exists(CredentialPath) || !Directory.EnumerateFileSystemEntries(CredentialPath).Any())
        {
            return false;
        }
        
        return await AuthenticateCoreAsync();
    }

    public async Task<bool> AuthenticateInteractiveAsync()
    {
        return await AuthenticateCoreAsync();
    }
    
    private async Task<bool> AuthenticateCoreAsync()
    {
        CancellationTokenSource? cts = null;

        try
        {
            cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = ClientId,
                    ClientSecret = ClientSecret
                },
                Scopes,
                "user",
                cts.Token,
                new EncryptedFileDataStore(CredentialPath));

            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            _appFolderId = await GetOrCreateAppFolderAsync();

            return true;
        }
        catch (OperationCanceledException)
        {
            _driveService?.Dispose();
            _driveService = null;
            _appFolderId = null;

            return false;
        }
        catch (Exception ex)
        {
            try
            {
                if (Directory.Exists(CredentialPath))
                    Directory.Delete(CredentialPath, true);
            }
            catch
            {
                // ignored
            }

            _driveService?.Dispose();
            _driveService = null;
            _appFolderId = null;

            System.Diagnostics.Debug.WriteLine($"[GoogleDrive] Auth error: {ex.Message}");
            return false;
        }
        finally
        {
            cts?.Dispose();
        }
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
                var updateMetadata = new Google.Apis.Drive.v3.Data.File { Name = remoteName };
                var updateRequest = _driveService.Files.Update(updateMetadata, existingFileId, stream,
                    "application/octet-stream");
                updateRequest.Fields = "id, name, size, modifiedTime";

                updateRequest.ProgressChanged += p =>
                {
                    if (totalSize > 0)
                        progress?.Report((double)p.BytesSent / totalSize * 100);
                };

                result = await updateRequest.UploadAsync();
                fileId = updateRequest.ResponseBody?.Id ?? existingFileId;
            }
            else
            {
                var fileMetadata = new Google.Apis.Drive.v3.Data.File
                {
                    Name = remoteName,
                    Parents = _appFolderId != null ? [_appFolderId] : null
                };

                var createRequest = _driveService.Files.Create(fileMetadata, stream, "application/octet-stream");
                createRequest.Fields = "id, name, size, modifiedTime";

                createRequest.ProgressChanged += p =>
                {
                    if (totalSize > 0)
                        progress?.Report((double)p.BytesSent / totalSize * 100);
                };

                result = await createRequest.UploadAsync();
                fileId = createRequest.ResponseBody?.Id ?? throw new Exception("アップロード後のファイルIDを取得できませんでした。");
            }

            if (result.Status != Google.Apis.Upload.UploadStatus.Completed)
                throw new Exception($"アップロードに失敗しました: {result.Exception?.Message}");

            return fileId;
        });
    }

    private async Task<string?> FindFileByNameAsync(string fileName)
    {
        if (_driveService == null || _appFolderId == null) return null;

        var listRequest = _driveService.Files.List();
        listRequest.Q = $"name = '{fileName.Replace("'", "\\'")}' and '{_appFolderId}' in parents and trashed = false";
        listRequest.Fields = "files(id)";

        var result = await listRequest.ExecuteAsync();
        return result.Files.FirstOrDefault()?.Id;
    }

    public async Task DownloadFileAsync(string remoteFileId, string localPath, IProgress<double>? progress = null)
    {
        if (_driveService == null)
            throw new InvalidOperationException("認証されていません。");
        
        var fileRequest = _driveService.Files.Get(remoteFileId);
        fileRequest.Fields = "size";
        var fileInfo = await fileRequest.ExecuteAsync();
        var totalSize = fileInfo.Size ?? 0;

        var request = _driveService.Files.Get(remoteFileId);

        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        var tempPath = localPath + ".tmp";
        
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                request.MediaDownloader.ProgressChanged += p =>
                {
                    if (totalSize > 0)
                    {
                        progress?.Report((double)p.BytesDownloaded / totalSize * 100);
                    }
                };

                await request.DownloadAsync(stream);
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

    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null)
    {
        if (_driveService == null)
            throw new InvalidOperationException("認証されていません。");

        var targetFolderId = folderId ?? _appFolderId;
        var files = new List<CloudFile>();
        string? pageToken = null;

        do
        {
            var request = _driveService.Files.List();
            request.Q = targetFolderId != null
                ? $"'{targetFolderId}' in parents and trashed = false"
                : "trashed = false";
            request.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
            request.OrderBy = "modifiedTime desc";
            request.PageSize = 100;
            
            if (pageToken != null)
                request.PageToken = pageToken;

            var result = await request.ExecuteAsync();

            files.AddRange(result.Files.Select(file => new CloudFile(file.Id, file.Name, file.MimeType, file.Size, file.ModifiedTimeDateTimeOffset?.DateTime)));

            pageToken = result.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return files;
    }

    public async Task DeleteFileAsync(string fileId)
    {
        if (_driveService == null)
            throw new InvalidOperationException("認証されていません。");

        await _driveService.Files.Delete(fileId).ExecuteAsync();
    }

    private async Task<string?> GetOrCreateAppFolderAsync()
    {
        if (_driveService == null) return null;

        var listRequest = _driveService.Files.List();
        listRequest.Q = $"name = '{FolderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        listRequest.Fields = "files(id, name)";

        var result = await listRequest.ExecuteAsync();

        if (result.Files.Count > 0)
        {
            return result.Files[0].Id;
        }

        var folderMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = FolderName,
            MimeType = "application/vnd.google-apps.folder"
        };

        var createRequest = _driveService.Files.Create(folderMetadata);
        createRequest.Fields = "id";

        var folder = await createRequest.ExecuteAsync();
        return folder.Id;
    }
}
