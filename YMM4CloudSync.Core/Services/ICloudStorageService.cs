namespace YMM4CloudSync.Core.Services;

/// <summary>
/// Interface for cloud storage service providers.
/// </summary>
public interface ICloudStorageService
{
    /// <summary>
    /// Gets the display name of the cloud service (e.g., "Google Drive", "OneDrive").
    /// </summary>
    string ServiceName { get; }

    string ConnectionKey { get; }

    /// <summary>
    /// Gets a value indicating whether the user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Attempts to authenticate silently using cached credentials.
    /// </summary>
    /// <returns>True if authentication succeeded; otherwise, false.</returns>
    Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs out the current user and clears cached credentials.
    /// </summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists files in the specified folder or the root application folder.
    /// </summary>
    /// <param name="folderId">The folder ID to list files from, or null for the root folder.</param>
    /// <returns>A list of cloud files with metadata.</returns>
    /// <exception cref="InvalidOperationException">Thrown when not authenticated.</exception>
    Task<List<CloudFile>> ListFilesAsync(string? folderId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file from cloud storage to a local path.
    /// </summary>
    /// <param name="remoteFileId">The unique ID of the file in cloud storage.</param>
    /// <param name="localPath">The destination path for the downloaded file.</param>
    /// <param name="progress">Optional progress reporter for download percentage (0-100).</param>
    /// <exception cref="InvalidOperationException">Thrown when not authenticated or download fails.</exception>
    Task DownloadFileAsync(string remoteFileId, string localPath, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file from cloud storage.
    /// </summary>
    /// <param name="fileId">The unique ID of the file to delete.</param>
    /// <exception cref="InvalidOperationException">Thrown when not authenticated or deletion fails.</exception>
    Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default);

    Task<CloudFile> CreateFolderAsync(string? parentId, string name,
        CancellationToken cancellationToken = default);

    Task<string> UploadFileToFolderAsync(string localPath, string? parentFolderId, string fileName,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    Task<AssetRootListing?> TryOpenAssetRootAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult<AssetRootListing?>(null);
}

public sealed record AssetRootListing(string FolderId, List<CloudFile> Files);

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
    DateTime? ModifiedTime,
    string? ParentId = null
)
{
    public bool IsFolder => CloudMimeTypes.IsFolder(MimeType);
}

public static class CloudMimeTypes
{
    public const string GoogleFolder = "application/vnd.google-apps.folder";
    public const string OneDriveFolder = "application/vnd.microsoft.folder";
    public const string DropboxFolder = "application/vnd.dropbox.folder";
    public const string WebDavCollection = "httpd/unix-directory";

    public static bool IsFolder(string? mimeType)
    {
        return mimeType is GoogleFolder or OneDriveFolder or DropboxFolder or WebDavCollection;
    }
}
