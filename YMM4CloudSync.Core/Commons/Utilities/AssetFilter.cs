using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Core.Commons.Utilities;

public sealed record AssetFilterCriteria(
    string? Query = null,
    bool ShowVideo = true,
    bool ShowAudio = true,
    bool ShowImage = true,
    bool ShowText = true,
    bool ShowOther = true,
    bool ShowFolder = true,
    bool DownloadedOnly = false)
{
    public static readonly AssetFilterCriteria None = new();

    public bool IsFilteredByType
        => !ShowVideo || !ShowAudio || !ShowImage || !ShowText || !ShowOther || !ShowFolder || DownloadedOnly;

    public bool IsFiltered => IsFilteredByType || !string.IsNullOrWhiteSpace(Query);
}

public static class AssetFilter
{
    public static bool Matches(AssetFilterCriteria criteria, string name, bool isFolder,
        AssetCategory category, AssetState state)
    {
        if (isFolder)
        {
            if (!criteria.ShowFolder) return false;
        }
        else
        {
            if (!IsCategoryVisible(criteria, category)) return false;

            if (criteria.DownloadedOnly && state is not (AssetState.Downloaded or AssetState.Stale)) return false;
        }

        return MatchesQuery(criteria.Query, name);
    }

    private static bool IsCategoryVisible(AssetFilterCriteria criteria, AssetCategory category) => category switch
    {
        AssetCategory.Video => criteria.ShowVideo,
        AssetCategory.Audio => criteria.ShowAudio,
        AssetCategory.Image => criteria.ShowImage,
        AssetCategory.Text => criteria.ShowText,
        AssetCategory.Folder => criteria.ShowFolder,
        _ => criteria.ShowOther
    };

    private static bool MatchesQuery(string? query, string name)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        return name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
