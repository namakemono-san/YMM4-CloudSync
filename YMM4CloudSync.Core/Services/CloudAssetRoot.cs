namespace YMM4CloudSync.Core.Services;

public static class CloudAssetRoot
{
    public const string FolderName = "Assets";

    public static async Task<string> EnsureAsync(ICloudStorageService service,
        CancellationToken cancellationToken = default)
    {
        var entries = await service.ListFilesAsync(null, cancellationToken);

        var match = entries.FirstOrDefault(e =>
            string.Equals(e.Name, FolderName, StringComparison.OrdinalIgnoreCase));

        if (match is { IsFolder: true }) return match.Id;

        if (match != null)
        {
            throw new InvalidOperationException(
                $"クラウド上に「{FolderName}」という名前のファイルが既に存在するため、素材フォルダを作成できません。\n" +
                "名前を変更するか削除してから、もう一度お試しください。");
        }

        var created = await service.CreateFolderAsync(null, FolderName, cancellationToken);

        return created.Id;
    }
}
