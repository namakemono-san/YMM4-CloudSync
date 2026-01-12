using System.IO;
using System.Text.Json;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Core.Commons.Configuration;

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "user_settings.json");

    public static UserSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new UserSettings();
        
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(dir) && dir != null) 
                Directory.CreateDirectory(dir);
            
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
        }
    }
}