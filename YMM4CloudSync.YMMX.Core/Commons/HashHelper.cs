using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class HashHelper
{
    private const int FileBufferSize = 81920;

    public static string ComputeDirectoryHash(
        string directory, 
        bool includeLegacyFiles,
        IProgress<double>? progress,
        long totalBytes,
        ref long processedBytes)
    {
        using var sha256 = SHA256.Create();
        
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase));

        if (!includeLegacyFiles)
        {
            files = files
                .Where(f => !Path.GetFileName(f).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase));
        }

        files = files.OrderBy(f => f, StringComparer.Ordinal);

        var buffer = new byte[FileBufferSize];

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            sha256.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);

                if (progress == null || totalBytes <= 0) continue;
                
                processedBytes += bytesRead;
                progress.Report((double)processedBytes / totalBytes * 100);
            }
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }
}