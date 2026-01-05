using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMM4CloudSync.YMMX.Core.Models;

public class YmmxMeta
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("format_version")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("plugin_version")]
    public required string PluginVersion { get; init; }

    [JsonPropertyName("min_plugin_version")]
    public required string MinPluginVersion { get; init; }

    public static YmmxMeta? Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<YmmxMeta>(json);
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }
}