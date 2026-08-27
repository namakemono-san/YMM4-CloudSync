using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

    private static readonly Lock Gate = new();

    internal static string? PathOverride { get; set; }

    public static string StatePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "open_state.json");

    public static OpenStateEntry? Find(string serviceName, string fileId)
    {
        lock (Gate)
        {
            return Load().FirstOrDefault(e => IsSame(e, serviceName, fileId));
        }
    }

    public static void Save(OpenStateEntry entry)
    {
        lock (Gate)
        {
            var entries = Load();

            entries.RemoveAll(e => IsSame(e, entry.ServiceName, entry.FileId));
            entries.Insert(0, entry);

            if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

            Write(entries);
        }
    }

    public static void Remove(string serviceName, string fileId)
    {
        lock (Gate)
        {
            var entries = Load();

            if (entries.RemoveAll(e => IsSame(e, serviceName, fileId)) == 0) return;

            Write(entries);
        }
    }

    private static bool IsSame(OpenStateEntry entry, string serviceName, string fileId)
        => string.Equals(entry.ServiceName, serviceName, StringComparison.Ordinal)
           && string.Equals(entry.FileId, fileId, StringComparison.Ordinal);

    private static List<OpenStateEntry> Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return [];

            using var stream = File.OpenRead(StatePath);

            var entries = JsonSerializer.Deserialize<List<OpenStateEntry>>(stream) ?? [];

            return entries.Where(e => !string.IsNullOrEmpty(e.YmmpPath) && File.Exists(e.YmmpPath)).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][OpenState] Failed to load: {ex.Message}");
            return [];
        }
    }

    private static void Write(List<OpenStateEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });

            var tempPath = StatePath + ".tmp";

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][OpenState] Failed to save: {ex.Message}");
        }
    }
}
