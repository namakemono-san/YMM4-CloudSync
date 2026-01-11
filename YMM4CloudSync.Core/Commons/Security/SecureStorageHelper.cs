using System.IO;
using System.Security.Cryptography;

namespace YMM4CloudSync.Core.Commons.Security;

public static class SecureStorageHelper
{
    public static byte[]? Load(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var encryptedData = File.ReadAllBytes(path);
            return ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encryptedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, encryptedData);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}