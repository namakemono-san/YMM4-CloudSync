using YMM4CloudSync.Core.Commons.Configuration;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class AssetStateResolver
{
    public static AssetState Resolve(DateTime? remoteModifiedTime, long? remoteSize,
        AssetStateEntry? entry, bool localExists)
    {
        if (entry is null || !localExists) return AssetState.NotDownloaded;

        return entry.Matches(remoteModifiedTime, remoteSize) ? AssetState.Downloaded : AssetState.Stale;
    }
}
