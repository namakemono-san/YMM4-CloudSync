using System.Reflection;
using YMM4CloudSync.YMMX.Core.Models;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class VersionChecker
{
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static bool IsCompatible(YmmxMeta meta)
    {
        var current = Version.Parse(CurrentVersion);
        var minRequired = Version.Parse(meta.MinPluginVersion);
        return current >= minRequired;
    }

    public static string? Validate(YmmxMeta meta)
    {
        return !IsCompatible(meta) ? $"このファイルにはプラグイン v{meta.MinPluginVersion} 以上が必要です。\n現在のバージョン: v{CurrentVersion}" : null;
    }
}