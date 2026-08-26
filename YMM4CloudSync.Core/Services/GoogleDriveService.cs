using System.Diagnostics;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.IO;
using YMM4CloudSync.Core.Commons;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Commons.Security;
using YMM4CloudSync.Core.Commons.Utilities;

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

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(CredentialPath) || !Directory.EnumerateFileSystemEntries(CredentialPath).Any())
        {
            return false;
        }

        return await AuthenticateCoreAsync(cancellationToken);
    }

    public async Task<bool> AuthenticateInteractiveAsync(CancellationToken cancellationToken = default)
    {
        return await AuthenticateCoreAsync(cancellationToken);
    }

    private async Task<bool> AuthenticateCoreAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
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

            var driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            _driveService = driveService;
            _appFolderId = await GetOrCreateAppFolderAsync(driveService, cts.Token);

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
            if (IsCredentialRejected(ex))
            {
                DeleteCredentials();
            }

            _driveService?.Dispose();
            _driveService = null;
            _appFolderId = null;

            SentryReporter.Capture(ex);
            Debug.WriteLine($"[GoogleDrive] Auth error: {ex.Message}");
            return false;
        }
    }

    private static bool IsCredentialRejected(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is not TokenResponseException tokenEx) continue;

            var error = tokenEx.Error?.Error;

            if (string.IsNullOrEmpty(error)) return true;

            if (error is "invalid_grant" or "invalid_client" or "unauthorized_client")
                return true;
        }

        return false;
    }

    private static void DeleteCredentials()
    {
        try
        {
            if (Directory.Exists(CredentialPath))
                Directory.Delete(CredentialPath, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GoogleDrive] Failed to delete credential directory: {ex.Message}");
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
    
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
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

    public async Task<string> UploadFileAsync(string localPath, string remoteName,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var driveService = EnsureAuthenticated();

        if (!File.Exists(localPath))
            throw new FileNotFoundException("アップロードするファイルが見つかりません。", localPath);

        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingFileId = await FindFileByNameAsync(driveService, remoteName, cancellationToken);

            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            var totalSize = stream.Length;

            Google.Apis.Upload.IUploadProgress result;
            string fileId;

            if (existingFileId != null)
            {
                var updateMetadata = new Google.Apis.Drive.v3.Data.File { Name = remoteName };
                var updateRequest = driveService.Files.Update(updateMetadata, existingFileId, stream,
                    "application/octet-stream");
                updateRequest.Fields = "id, name, size, modifiedTime";

                updateRequest.ProgressChanged += p =>
                {
                    if (totalSize > 0)
                        progress?.Report((double)p.BytesSent / totalSize * 100);
                };

                result = await updateRequest.UploadAsync(cancellationToken);
                fileId = updateRequest.ResponseBody?.Id ?? existingFileId;
            }
            else
            {
                var fileMetadata = new Google.Apis.Drive.v3.Data.File
                {
                    Name = remoteName,
                    Parents = _appFolderId != null ? [_appFolderId] : null
                };

                var createRequest = driveService.Files.Create(fileMetadata, stream, "application/octet-stream");
                createRequest.Fields = "id, name, size, modifiedTime";

                createRequest.ProgressChanged += p =>
                {
                    if (totalSize > 0)
                        progress?.Report((double)p.BytesSent / totalSize * 100);
                };

                result = await createRequest.UploadAsync(cancellationToken);
                fileId = createRequest.ResponseBody?.Id ?? throw new Exception("アップロード後のファイルIDを取得できませんでした。");
            }

            if (result.Status != Google.Apis.Upload.UploadStatus.Completed)
            {
                if (result.Exception is OperationCanceledException) throw result.Exception;
                cancellationToken.ThrowIfCancellationRequested();

                throw new Exception($"アップロードに失敗しました: {result.Exception?.Message}");
            }

            return fileId;
        }, cancellationToken: cancellationToken);
    }

    private async Task<string?> FindFileByNameAsync(DriveService driveService, string fileName, CancellationToken cancellationToken)
    {
        if (_appFolderId == null) return null;

        var listRequest = driveService.Files.List();
        listRequest.Q = $"name = '{EscapeQueryValue(fileName)}' and '{_appFolderId}' in parents and trashed = false";
        listRequest.Fields = "files(id)";

        var result = await listRequest.ExecuteAsync(cancellationToken);
        return result.Files.FirstOrDefault()?.Id;
    }

    private static string EscapeQueryValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    public async Task DownloadFileAsync(string remoteFileId, string localPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var driveService = EnsureAuthenticated();

        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = localPath + ".tmp";

        try
        {
            await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileRequest = driveService.Files.Get(remoteFileId);
                fileRequest.Fields = "size";
                var fileInfo = await fileRequest.ExecuteAsync(cancellationToken);
                var totalSize = fileInfo.Size ?? 0;

                var request = driveService.Files.Get(remoteFileId);

                await using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);

                request.MediaDownloader.ProgressChanged += p =>
                {
                    if (totalSize > 0)
                    {
                        progress?.Report((double)p.BytesDownloaded / totalSize * 100);
                    }
                };

                await request.DownloadAsync(stream, cancellationToken);
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
            Debug.WriteLine($"[GoogleDrive] Failed to delete temporary file: {ex.Message}");
        }
    }

    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var driveService = EnsureAuthenticated();

        var targetFolderId = folderId ?? _appFolderId;
        var files = new List<CloudFile>();
        string? pageToken = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentPageToken = pageToken;

            var result = await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                var request = driveService.Files.List();
                request.Q = targetFolderId != null
                    ? $"'{EscapeQueryValue(targetFolderId)}' in parents and trashed = false"
                    : "trashed = false";
                request.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
                request.OrderBy = "modifiedTime desc";
                request.PageSize = 100;

                if (currentPageToken != null)
                    request.PageToken = currentPageToken;

                return await request.ExecuteAsync(cancellationToken);
            }, cancellationToken: cancellationToken);

            if (result.Files != null)
            {
                files.AddRange(result.Files.Select(file => new CloudFile(file.Id, file.Name, file.MimeType, file.Size, file.ModifiedTimeDateTimeOffset?.DateTime)));
            }

            pageToken = result.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return files;
    }

    public async Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var driveService = EnsureAuthenticated();

        await RetryHelper.ExecuteWithRetryAsync(
            () => driveService.Files.Delete(fileId).ExecuteAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private DriveService EnsureAuthenticated()
    {
        return _driveService
               ?? throw new InvalidOperationException("認証されていません。");
    }

    private async Task<string?> GetOrCreateAppFolderAsync(DriveService driveService, CancellationToken cancellationToken)
    {
        var listRequest = driveService.Files.List();
        listRequest.Q = $"name = '{FolderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        listRequest.Fields = "files(id, name)";

        var result = await listRequest.ExecuteAsync(cancellationToken);

        if (result.Files.Count > 0)
        {
            return result.Files[0].Id;
        }

        var folderMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = FolderName,
            MimeType = "application/vnd.google-apps.folder"
        };

        var createRequest = driveService.Files.Create(folderMetadata);
        createRequest.Fields = "id";

        var folder = await createRequest.ExecuteAsync(cancellationToken);
        return folder.Id;
    }
}
