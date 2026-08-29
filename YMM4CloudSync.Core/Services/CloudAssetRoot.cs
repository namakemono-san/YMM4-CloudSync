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

        if (match is { IsFolder: true } && !string.IsNullOrEmpty(match.Id)) return match.Id;

        if (match != null)
        {
            throw new InvalidOperationException(
                $"クラウド上に「{FolderName}」という名前のファイルが既に存在するため、素材フォルダを作成できません。\n" +
                "名前を変更するか削除してから、もう一度お試しください。");
        }

        var created = await service.CreateFolderAsync(null, FolderName, cancellationToken);

        if (string.IsNullOrEmpty(created.Id))
        {
            throw new InvalidOperationException(
                $"クラウド上に「{FolderName}」フォルダーを作成しましたが、識別子を取得できませんでした。\n" +
                "連携を解除して接続し直してから、もう一度お試しください。");
        }

        return created.Id;
    }
}
