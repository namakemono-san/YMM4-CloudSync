using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class UserSettingsReader
{
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "user_settings.json");

    public static string? ReadProjectDirectory()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;

            using var stream = File.OpenRead(SettingsPath);
            using var document = JsonDocument.Parse(stream);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("ProjectDirectory")) continue;

                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][Settings] Failed to read project directory: {ex.Message}");
            return null;
        }
    }
}
