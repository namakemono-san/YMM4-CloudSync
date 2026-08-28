using YMM4CloudSync.YMMX.Core.Commons;
using YukkuriMovieMaker.Commons;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class PathHelper
{
    public static string DefaultProjectDirectory => PathTagResolver.DefaultProjectDirectory;

    public static string DefaultCacheDirectory => PathTagResolver.DefaultCacheDirectory;

    public static string DefaultAssetDirectory => PathTagResolver.DefaultAssetDirectory;

    public static string ResolvePath(string? rawPath, string? projectDirectory = null)
        => PathTagResolver.Resolve(rawPath, projectDirectory, GetYmmUserDirectory());

    public static string ResolveProjectDirectory(string? rawProjectDirectory)
        => PathTagResolver.ResolveProjectDirectory(rawProjectDirectory, GetYmmUserDirectory());

    public static string SanitizeFileName(string? fileName, string fallback)
        => PathTagResolver.SanitizeFileName(fileName, fallback);

    public static string CombineWithin(string baseDirectory, string? fileName, string fallbackName)
        => PathTagResolver.CombineWithin(baseDirectory, fileName, fallbackName);

    public static string CombineWithin(string baseDirectory, IReadOnlyList<string> segments, string fallbackName)
        => PathTagResolver.CombineWithin(baseDirectory, segments, fallbackName);

    private static string? GetYmmUserDirectory()
    {
        try
        {
            return AppDirectories.UserDirectory;
        }
        catch
        {
            return null;
        }
    }
}
