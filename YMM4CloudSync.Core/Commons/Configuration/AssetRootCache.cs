using System.IO;
using System.Text.Json.Serialization;

namespace YMM4CloudSync.Core.Commons.Configuration;

public sealed class AssetRootEntry
{
    [JsonPropertyName("connection")]
    public string ConnectionKey { get; set; } = "";

    [JsonPropertyName("folder_id")]
    public string FolderId { get; set; } = "";
}

public static class AssetRootCache
{
    private static readonly JsonListStore<AssetRootEntry> Store = new(
        () => StatePath,
        e => !string.IsNullOrEmpty(e.ConnectionKey) && !string.IsNullOrEmpty(e.FolderId),
        "AssetRoot");

    internal static string? PathOverride { get; set; }

    public static string StatePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "asset_roots.json");

    public static string? Find(string connectionKey)
        => Store.Read(entries => entries
            .FirstOrDefault(e => string.Equals(e.ConnectionKey, connectionKey, StringComparison.Ordinal))?
            .FolderId);

    public static void Save(string connectionKey, string folderId)
    {
        if (string.IsNullOrEmpty(connectionKey) || string.IsNullOrEmpty(folderId)) return;

        Store.Update(entries =>
        {
            var existing = entries.FirstOrDefault(e =>
                string.Equals(e.ConnectionKey, connectionKey, StringComparison.Ordinal));

            if (string.Equals(existing?.FolderId, folderId, StringComparison.Ordinal)) return false;

            entries.RemoveAll(e => string.Equals(e.ConnectionKey, connectionKey, StringComparison.Ordinal));

            entries.Insert(0, new AssetRootEntry { ConnectionKey = connectionKey, FolderId = folderId });

            return true;
        });
    }

    public static void Forget(string connectionKey)
        => Store.Update(entries => entries.RemoveAll(e =>
            string.Equals(e.ConnectionKey, connectionKey, StringComparison.Ordinal)) > 0);
}
