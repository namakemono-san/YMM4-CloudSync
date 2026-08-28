namespace YMM4CloudSync.Core.Models;

public enum AssetState
{
    NotDownloaded,
    Downloading,
    Downloaded,
    Stale,
    Failed
}

public enum AssetCategory
{
    Folder,
    Video,
    Image,
    Audio,
    Text,
    Other
}

public enum AssetViewMode
{
    List,
    WrapList,
    Tiles
}
