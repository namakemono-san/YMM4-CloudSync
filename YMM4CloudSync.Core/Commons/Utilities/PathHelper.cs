using YukkuriMovieMaker.Commons;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class PathHelper
{
    public static string ResolvePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "";

        try
        {
            var path = rawPath
                .Replace("<YMMUserDir>", AppDirectories.UserDirectory)
                .Replace("<Desktop>", Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
                .Replace("<Documents>", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

            return path;
        }
        catch
        {
            return rawPath;
        }
    }
}