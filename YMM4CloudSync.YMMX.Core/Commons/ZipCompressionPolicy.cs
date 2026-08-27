using System.IO;
using System.IO.Compression;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class ZipCompressionPolicy
{
    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".flv", ".ts", ".mts", ".m2ts",
        ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma",
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".heic", ".heif", ".avif",
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".ymmx"
    };

    private static readonly HashSet<string> LightlyCompressible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd", ".psb", ".wav", ".aif", ".aiff", ".bmp", ".tif", ".tiff", ".tga", ".dds", ".exr"
    };

    public static CompressionLevel ForEntry(string relativeDestination)
    {
        var extension = Path.GetExtension(relativeDestination);

        if (string.IsNullOrEmpty(extension)) return CompressionLevel.Optimal;

        if (AlreadyCompressed.Contains(extension)) return CompressionLevel.NoCompression;

        return LightlyCompressible.Contains(extension) ? CompressionLevel.Fastest : CompressionLevel.Optimal;
    }
}
