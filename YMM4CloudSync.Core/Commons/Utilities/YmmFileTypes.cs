using System.Diagnostics;
using YMM4CloudSync.Core.Models;
using YukkuriMovieMaker.Settings;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class YmmFileTypes
{
    public static AssetCategory Classify(string fileName, bool isFolder)
    {
        if (isFolder) return AssetCategory.Folder;

        return TryClassifyWithYmm(fileName) ?? AssetFileTypes.Classify(fileName, false);
    }

    private static AssetCategory? TryClassifyWithYmm(string fileName)
    {
        try
        {
            var type = FileSettings.Default.FileExtensions.GetFileType(fileName);

            if (type.HasFlag(FileType.動画)) return AssetCategory.Video;
            if (type.HasFlag(FileType.画像)) return AssetCategory.Image;

            return type == FileType.音声 ? AssetCategory.Audio : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AssetTab] YMM4 file type lookup failed: {ex.Message}");
            return null;
        }
    }
}
