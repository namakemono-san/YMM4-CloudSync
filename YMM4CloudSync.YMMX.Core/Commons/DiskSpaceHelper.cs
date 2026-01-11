using System.IO;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class DiskSpaceHelper
{
    public static void EnsureFreeSpace(string path, long requiredBytes, string context = "保存先")
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"{context}のドライブ( {root} )の空き領域が不足しています。\n" +
                    $"必要サイズ: {FormatSize(requiredBytes)}\n" +
                    $"空き容量: {FormatSize(drive.AvailableFreeSpace)}\n\n" +
                    "不要なファイルを削除してから再試行してください。");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // ignore
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    public static bool IsDiskFull(Exception ex)
    {
        if (ex is not IOException ioEx) return false;

        const int hrErrorDiskFull = unchecked((int)0x80070070);
        const int hrErrorHandleDiskFull = unchecked((int)0x80070027);
        
        return ioEx.HResult is hrErrorDiskFull or hrErrorHandleDiskFull;
    }
}