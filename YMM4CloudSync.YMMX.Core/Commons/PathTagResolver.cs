using System.IO;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class PathTagResolver
{
    private const string ProjectDirTag = "<ProjectDir>";
    private const string YmmUserDirTag = "<YMMUserDir>";

    public static string DefaultProjectDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4CloudSync", "Projects");

    public static string DefaultCacheDirectory => Path.Combine(
        Path.GetTempPath(), "YMM4CloudSync");

    public static string Resolve(string? rawPath, string? projectDirectory, string? ymmUserDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "";

        try
        {
            return ResolveKnownTags(rawPath, ymmUserDirectory)
                .Replace(ProjectDirTag, ResolveProjectDirectory(projectDirectory, ymmUserDirectory));
        }
        catch
        {
            return rawPath;
        }
    }

    public static string ResolveProjectDirectory(string? rawProjectDirectory, string? ymmUserDirectory)
    {
        var resolved = string.IsNullOrWhiteSpace(rawProjectDirectory)
            ? ""
            : ResolveKnownTags(rawProjectDirectory, ymmUserDirectory).Replace(ProjectDirTag, DefaultProjectDirectory);

        return string.IsNullOrWhiteSpace(resolved) ? DefaultProjectDirectory : resolved;
    }

    public static string SanitizeFileName(string? fileName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fallback;

        var name = fileName.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        name = Path.GetInvalidFileNameChars().Aggregate(name, (current, c) => current.Replace(c, '_')).Trim();

        while (name.EndsWith('.')) name = name[..^1].Trim();

        return name is "" or "." or ".." ? fallback : name;
    }

    public static string CombineWithin(string baseDirectory, string? fileName, string fallbackName)
    {
        var safeName = SanitizeFileName(fileName, fallbackName);

        var root = Path.GetFullPath(baseDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(root, safeName));

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"ファイル名 \"{fileName}\" は保存先の外を指しています。処理を中止しました。");
        }

        return combined;
    }

    private static readonly Lazy<string> DiscoveredYmmUserDirectory =
        new(FindYmmUserDirectory, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string ResolveKnownTags(string path, string? ymmUserDirectory)
    {
        if (path.Contains(YmmUserDirTag, StringComparison.Ordinal))
        {
            var userDirectory = string.IsNullOrWhiteSpace(ymmUserDirectory)
                ? DiscoveredYmmUserDirectory.Value
                : ymmUserDirectory;

            path = path.Replace(YmmUserDirTag, userDirectory);
        }

        return path
            .Replace("<Desktop>", Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
            .Replace("<Documents>", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    private static string FindYmmUserDirectory()
    {
        var exePath = YmmPathFinder.Find();
        if (string.IsNullOrEmpty(exePath)) return "";

        var exeDirectory = Path.GetDirectoryName(exePath);

        return string.IsNullOrEmpty(exeDirectory) ? "" : Path.Combine(exeDirectory, "user");
    }
}
