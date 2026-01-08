using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace YMM4CloudSync.Core.Commons.License;

public static class LicenseLoader
{
    public static IReadOnlyList<LicenseFile> Load()
    {
        var pluginDir =
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        
        var licenseDir = Path.Combine(pluginDir, "Resources", "License");

        var result = new List<LicenseFile>();

        if (!Directory.Exists(licenseDir))
            return result;

        foreach (var file in Directory.GetFiles(licenseDir))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var text = File.ReadAllText(file);
                
                result.Add(new LicenseFile(name, text));
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
            }
        }

        return result;
    }
}
