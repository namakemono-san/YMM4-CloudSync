using System.IO;
using System.Text.Json.Serialization;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.Commons.Configuration;

public sealed class CachedCloudFile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mime")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("modified")]
    public DateTime? ModifiedTime { get; set; }

    [JsonPropertyName("parent")]
    public string? ParentId { get; set; }

    public static CachedCloudFile From(CloudFile file) => new()
    {
        Id = file.Id,
        Name = file.Name,
        MimeType = file.MimeType,
        Size = file.Size,
        ModifiedTime = file.ModifiedTime,
        ParentId = file.ParentId
    };

    public CloudFile ToCloudFile() => new(Id, Name, MimeType, Size, ModifiedTime, ParentId);
}

public sealed class AssetListingEntry
{
    [JsonPropertyName("connection")]
    public string ConnectionKey { get; set; } = "";

    [JsonPropertyName("folder_id")]
    public string FolderId { get; set; } = "";

    [JsonPropertyName("files")]
    public List<CachedCloudFile> Files { get; set; } = [];
}

public static class AssetListingCache
{
    private const int MaxFolders = 50;

    private static readonly JsonListStore<AssetListingEntry> Store = new(
        () => StatePath,
        e => !string.IsNullOrEmpty(e.ConnectionKey) && !string.IsNullOrEmpty(e.FolderId),
        "AssetListing");

    internal static string? PathOverride { get; set; }

    public static string StatePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "asset_listing.json");

    public static List<CloudFile>? Find(string connectionKey, string folderId)
        => Store.Read(entries => entries
            .FirstOrDefault(e => IsSame(e, connectionKey, folderId))?
            .Files.Select(f => f.ToCloudFile())
            .ToList());

    public static void Save(string connectionKey, string folderId, IReadOnlyList<CloudFile> files)
    {
        if (string.IsNullOrEmpty(connectionKey) || string.IsNullOrEmpty(folderId)) return;

        Store.Update(entries =>
        {
            entries.RemoveAll(e => IsSame(e, connectionKey, folderId));

            entries.Insert(0, new AssetListingEntry
            {
                ConnectionKey = connectionKey,
                FolderId = folderId,
                Files = [.. files.Select(CachedCloudFile.From)]
            });

            if (entries.Count > MaxFolders)
                entries.RemoveRange(MaxFolders, entries.Count - MaxFolders);

            return true;
        });
    }

    public static void Forget(string connectionKey, string folderId)
        => Store.Update(entries => entries.RemoveAll(e => IsSame(e, connectionKey, folderId)) > 0);

    public static void ForgetConnection(string connectionKey)
        => Store.Update(entries => entries.RemoveAll(e =>
            string.Equals(e.ConnectionKey, connectionKey, StringComparison.Ordinal)) > 0);

    private static bool IsSame(AssetListingEntry entry, string connectionKey, string folderId)
        => string.Equals(entry.ConnectionKey, connectionKey, StringComparison.Ordinal)
           && string.Equals(entry.FolderId, folderId, StringComparison.Ordinal);
}
