using System.IO;
using System.Security.Cryptography;

namespace YMM4CloudSync.Core.Commons.Security;

public static class SecureStorageHelper
{
    private const int LockRetryCount = 5;
    private const int LockRetryDelayMs = 60;

    public static byte[]? Load(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var encryptedData = WithLockRetry(() => File.ReadAllBytes(path));
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

        var tempPath = path + ".tmp";

        try
        {
            File.WriteAllBytes(tempPath, encryptedData);

            WithLockRetry(() =>
            {
                File.Move(tempPath, path, overwrite: true);
                return true;
            });
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignored */ }
            }

            throw;
        }
    }

    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;

        WithLockRetry(() =>
        {
            File.Delete(path);
            return true;
        });
    }

    private static T WithLockRetry<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (attempt < LockRetryCount)
            {
                Thread.Sleep(LockRetryDelayMs * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < LockRetryCount)
            {
                Thread.Sleep(LockRetryDelayMs * (attempt + 1));
            }
        }
    }
}