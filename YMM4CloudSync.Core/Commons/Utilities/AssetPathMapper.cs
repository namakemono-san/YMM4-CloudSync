using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class AssetPathMapper
{
    public const string FallbackName = "asset";

    public static string GetLocalPath(string assetDirectory, string connectionKey,
        IReadOnlyList<string> folderSegments, string fileName)
    {
        var segments = new List<string>(folderSegments.Count + 2) { connectionKey };

        segments.AddRange(folderSegments);
        segments.Add(fileName);

        return PathTagResolver.CombineWithin(assetDirectory, segments, FallbackName);
    }

    public static string GetLocalFolder(string assetDirectory, string connectionKey,
        IReadOnlyList<string> folderSegments)
    {
        var segments = new List<string>(folderSegments.Count + 1) { connectionKey };

        segments.AddRange(folderSegments);

        return PathTagResolver.CombineWithin(assetDirectory, segments, FallbackName);
    }
}
