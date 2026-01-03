using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.IO;

namespace YMM4CloudSync.Core.Services;

public class GoogleDriveService : ICloudStorageService
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

    public bool IsAuthenticated => _driveService != null;

    public async Task<bool> AuthenticateAsync()
    {
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
                CancellationToken.None,
                new FileDataStore(CredentialPath, true));

            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            _appFolderId = await GetOrCreateAppFolderAsync();

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GoogleDrive] Auth error: {ex.Message}");
            return false;
        }
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

        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = remoteName,
            Parents = _appFolderId != null ? new List<string> { _appFolderId } : null
        };

        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
        var totalSize = stream.Length;

        var request = _driveService.Files.Create(fileMetadata, stream, "application/octet-stream");
        request.Fields = "id, name, size, modifiedTime";

        request.ProgressChanged += p =>
        {
            if (totalSize > 0)
            {
                progress?.Report((double)p.BytesSent / totalSize * 100);
            }
        };

        var result = await request.UploadAsync();

        if (result.Status != Google.Apis.Upload.UploadStatus.Completed)
        {
            throw new Exception($"アップロードに失敗しました: {result.Exception?.Message}");
        }

        return request.ResponseBody.Id;
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

        await using var stream = new FileStream(localPath, FileMode.Create, FileAccess.Write);

        request.MediaDownloader.ProgressChanged += p =>
        {
            if (totalSize > 0)
            {
                progress?.Report((double)p.BytesDownloaded / totalSize * 100);
            }
        };

        await request.DownloadAsync(stream);
    }

    public async Task<List<CloudFile>> ListFilesAsync(string? folderId = null)
    {
        if (_driveService == null)
            throw new InvalidOperationException("認証されていません。");

        var targetFolderId = folderId ?? _appFolderId;
        var files = new List<CloudFile>();

        var request = _driveService.Files.List();
        request.Q = targetFolderId != null
            ? $"'{targetFolderId}' in parents and trashed = false"
            : "trashed = false";
        request.Fields = "files(id, name, mimeType, size, modifiedTime)";
        request.OrderBy = "modifiedTime desc";

        var result = await request.ExecuteAsync();

        foreach (var file in result.Files)
        {
            files.Add(new CloudFile(
                file.Id,
                file.Name,
                file.MimeType,
                file.Size,
                file.ModifiedTimeDateTimeOffset?.DateTime
            ));
        }

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