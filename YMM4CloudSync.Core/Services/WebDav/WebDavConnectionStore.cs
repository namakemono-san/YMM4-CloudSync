using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using YMM4CloudSync.Core.Commons.Security;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Core.Services.WebDav;

public static class WebDavConnectionStore
{
    private const string CredentialFileName = "webdav_credentials.bin";

    private static readonly Lock Gate = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string CredentialPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", CredentialFileName);

    public static List<WebDavSettings> Load()
    {
        lock (Gate)
        {
            try
            {
                var data = SecureStorageHelper.Load(CredentialPath);

                if (data == null || data.Length == 0) return [];

                return Deserialize(Encoding.UTF8.GetString(data));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebDAV] Failed to load connections: {ex.Message}");
                return [];
            }
        }
    }

    internal static List<WebDavSettings> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var document = JsonDocument.Parse(json);

        var result = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<WebDavSettings>>(json, SerializerOptions) ?? [],
            JsonValueKind.Object => Single(JsonSerializer.Deserialize<WebDavSettings>(json, SerializerOptions)),
            _ => []
        };

        foreach (var settings in result.Where(settings => string.IsNullOrWhiteSpace(settings.Id)))
        {
            settings.Id = Guid.NewGuid().ToString("N");
        }

        return result;
    }

    private static List<WebDavSettings> Single(WebDavSettings? settings)
        => settings == null ? [] : [settings];

    public static void Save(IEnumerable<WebDavSettings> connections)
    {
        lock (Gate)
        {
            var list = connections.ToList();

            if (list.Count == 0)
            {
                SecureStorageHelper.Delete(CredentialPath);
                return;
            }

            var json = JsonSerializer.Serialize(list, SerializerOptions);
            SecureStorageHelper.Save(CredentialPath, Encoding.UTF8.GetBytes(json));
        }
    }

    public static void Upsert(WebDavSettings settings)
    {
        var connections = Load();

        var index = connections.FindIndex(c => c.Id == settings.Id);

        if (index >= 0) connections[index] = settings;
        else connections.Add(settings);

        Save(connections);
    }

    public static void Remove(string id)
    {
        var connections = Load();

        if (connections.RemoveAll(c => c.Id == id) == 0) return;

        Save(connections);
    }
}
