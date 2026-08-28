using System.IO;
using System.Text.Json.Serialization;

namespace YMM4CloudSync.Core.Commons.Configuration;

public sealed class OpenStateEntry
{
    [JsonPropertyName("service")]
    public string ServiceName { get; set; } = "";

    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("remote_modified_time")]
    public DateTime? RemoteModifiedTime { get; set; }

    [JsonPropertyName("remote_size")]
    public long? RemoteSize { get; set; }

    [JsonPropertyName("ymmp_path")]
    public string YmmpPath { get; set; } = "";

    public bool Matches(DateTime? remoteModifiedTime, long? remoteSize)
        => Nullable.Equals(RemoteModifiedTime, remoteModifiedTime) && RemoteSize == remoteSize;
}

public static class OpenStateStore
{
    private const int MaxEntries = 200;

    private static readonly JsonListStore<OpenStateEntry> Store = new(
        () => StatePath,
        e => !string.IsNullOrEmpty(e.YmmpPath) && File.Exists(e.YmmpPath),
        "OpenState");

    internal static string? PathOverride { get; set; }

    public static string StatePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "open_state.json");

    public static OpenStateEntry? Find(string serviceName, string fileId)
        => Store.Read(entries => entries.FirstOrDefault(e => IsSame(e, serviceName, fileId)));

    public static void Save(OpenStateEntry entry)
    {
        Store.Update(entries =>
        {
            entries.RemoveAll(e => IsSame(e, entry.ServiceName, entry.FileId));
            entries.Insert(0, entry);

            if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

            return true;
        });
    }

    public static void Remove(string serviceName, string fileId)
        => Store.Update(entries => entries.RemoveAll(e => IsSame(e, serviceName, fileId)) > 0);

    private static bool IsSame(OpenStateEntry entry, string serviceName, string fileId)
        => string.Equals(entry.ServiceName, serviceName, StringComparison.Ordinal)
           && string.Equals(entry.FileId, fileId, StringComparison.Ordinal);
}
