using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YMM4CloudSync.Core.Commons.Configuration;

internal sealed class JsonListStore<T>(Func<string> pathProvider, Func<T, bool> isAlive, string logTag)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();

    public string FilePath => pathProvider();

    public TResult Read<TResult>(Func<List<T>, TResult> selector)
    {
        lock (_gate)
        {
            return selector(Load());
        }
    }

    public void Update(Func<List<T>, bool> mutate)
    {
        lock (_gate)
        {
            var entries = Load();

            if (!mutate(entries)) return;

            Write(entries);
        }
    }

    private List<T> Load()
    {
        var path = FilePath;

        try
        {
            if (!File.Exists(path)) return [];

            using var stream = File.OpenRead(path);

            var entries = JsonSerializer.Deserialize<List<T>>(stream) ?? [];

            return entries.Where(isAlive).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][{logTag}] Failed to load: {ex.Message}");
            return [];
        }
    }

    private void Write(List<T> entries)
    {
        var path = FilePath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(entries, WriteOptions);

            var tempPath = path + ".tmp";

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][{logTag}] Failed to save: {ex.Message}");
        }
    }
}
