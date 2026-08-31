using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static partial class ImageSequence
{
    private static readonly HashSet<string> StillImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    [GeneratedRegex("[0-9]+$")]
    private static partial Regex TailNumberRegex();

    public static bool IsStillImage(string path)
        => StillImageExtensions.Contains(Path.GetExtension(path));

    public static List<string>? TryGetFrames(string filePath)
    {
        if (!IsStillImage(filePath)) return null;

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return null;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        var match = TailNumberRegex().Match(nameWithoutExtension);

        if (!match.Success || !int.TryParse(match.Value, out var startIndex)) return null;

        var prefix = TailNumberRegex().Replace(nameWithoutExtension, "");
        var extension = Path.GetExtension(filePath);

        try
        {
            var headLength = Path.Combine(directory, prefix).Length;

            var ordered = Directory
                .GetFiles(directory, prefix + "*" + extension)
                .Select(path =>
                {
                    var middle = path[headLength..^extension.Length];

                    return (Path: path, Parsed: int.TryParse(middle, out var index), Index: index);
                })
                .Where(x => x.Parsed)
                .OrderBy(x => x.Index)
                .ToList();

            var expected = startIndex;
            var frames = new List<string>();

            foreach (var candidate in ordered)
            {
                if (candidate.Index != expected) break;

                frames.Add(candidate.Path);
                expected++;
            }

            return frames.Count > 1 ? frames : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageSequence] Failed to enumerate frames for {filePath}: {ex.Message}");
            return null;
        }
    }
}
