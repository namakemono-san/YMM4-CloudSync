namespace YMM4CloudSync.YMMX.Core.Commons;

public static class ExternalReferenceNotice
{
    private const int SampleCount = 5;

    public static string Build(IReadOnlyList<string> externalReferences)
    {
        var sample = string.Join("\n", externalReferences.Take(SampleCount));
        var more = externalReferences.Count > SampleCount
            ? $"\n… 他 {externalReferences.Count - SampleCount} 件"
            : "";

        return $"このプロジェクトには、パッケージに含まれていないファイルへの参照が {externalReferences.Count} 件あります。\n" +
               "該当する素材は読み込まれません。\n\n" +
               "保存元でファイルが見つからなかったか、パッケージの外を指す参照が取り除かれています。\n\n" +
               $"{sample}{more}";
    }
}
