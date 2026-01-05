namespace YMM4CloudSync.Core.Services;

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

public record CloudFile(
    string Id,
    string Name,
    string MimeType,
    long? Size,
    DateTime? ModifiedTime
);