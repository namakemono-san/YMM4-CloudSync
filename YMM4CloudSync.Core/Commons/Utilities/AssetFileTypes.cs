using System.IO;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class AssetFileTypes
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg"
    };

    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".tga", ".heic", ".heif", ".avif",
        ".psd", ".psb", ".svg", ".ico"
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aif", ".aiff", ".mid", ".midi"
    };

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".json", ".xml", ".exo", ".ymmp", ".ymmx", ".lab", ".srt", ".ass"
    };

    public static AssetCategory Classify(string fileName, bool isFolder)
    {
        if (isFolder) return AssetCategory.Folder;

        var extension = Path.GetExtension(fileName);

        if (string.IsNullOrEmpty(extension)) return AssetCategory.Other;

        if (Video.Contains(extension)) return AssetCategory.Video;
        if (Image.Contains(extension)) return AssetCategory.Image;
        if (Audio.Contains(extension)) return AssetCategory.Audio;

        return Text.Contains(extension) ? AssetCategory.Text : AssetCategory.Other;
    }
}
