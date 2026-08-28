using System.IO;
using System.Text.Json.Serialization;

namespace YMM4CloudSync.Core.Commons.Configuration;

public sealed class AssetStateEntry
{
    [JsonPropertyName("connection")]
    public string ConnectionKey { get; set; } = "";

    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("remote_modified_time")]
    public DateTime? RemoteModifiedTime { get; set; }

    [JsonPropertyName("remote_size")]
    public long? RemoteSize { get; set; }

    [JsonPropertyName("local_path")]
    public string LocalPath { get; set; } = "";

    [JsonPropertyName("remote_parent_id")]
    public string? RemoteParentId { get; set; }

    public bool Matches(DateTime? remoteModifiedTime, long? remoteSize)
        => Nullable.Equals(RemoteModifiedTime, remoteModifiedTime) && RemoteSize == remoteSize;
}

public static class AssetStateStore
{
    private static readonly JsonListStore<AssetStateEntry> Store = new(
        () => StatePath,
        e => !string.IsNullOrEmpty(e.LocalPath) && File.Exists(e.LocalPath),
        "AssetState");

    internal static string? PathOverride { get; set; }

    public static string StatePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "asset_state.json");

    public static AssetStateEntry? Find(string connectionKey, string fileId)
        => Store.Read(entries => entries.FirstOrDefault(e => IsSame(e, connectionKey, fileId)));

    public static List<AssetStateEntry> FindAll(string connectionKey)
        => Store.Read(entries => entries
            .Where(e => string.Equals(e.ConnectionKey, connectionKey, StringComparison.Ordinal))
            .ToList());

    public static void Save(AssetStateEntry entry)
    {
        Store.Update(entries =>
        {
            entries.RemoveAll(e => IsSame(e, entry.ConnectionKey, entry.FileId));
            entries.Insert(0, entry);

            return true;
        });
    }

    public static void Remove(string connectionKey, string fileId)
        => Store.Update(entries => entries.RemoveAll(e => IsSame(e, connectionKey, fileId)) > 0);

    private static bool IsSame(AssetStateEntry entry, string connectionKey, string fileId)
        => string.Equals(entry.ConnectionKey, connectionKey, StringComparison.Ordinal)
           && string.Equals(entry.FileId, fileId, StringComparison.Ordinal);
}
