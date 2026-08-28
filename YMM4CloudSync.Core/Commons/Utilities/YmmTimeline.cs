using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using YukkuriMovieMaker.Settings;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class YmmTimeline
{
    public static bool TryAddFiles(IReadOnlyList<string> paths, IInputElement? source)
    {
        if (paths.Count == 0) return false;

        try
        {
            if (CommandSettings.Default[CommandType.AddFileItem] is not { } command) return false;

            var parameter = paths.ToArray();

            foreach (var target in EnumerateTargets(source))
            {
                if (!command.CanExecute(parameter, target)) continue;

                command.Execute(parameter, target);

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AssetTab] Failed to add files to the timeline: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<IInputElement> EnumerateTargets(IInputElement? source)
    {
        if (IsRoutable(source)) yield return source!;

        var focused = Keyboard.FocusedElement;

        if (IsRoutable(focused) && !ReferenceEquals(focused, source)) yield return focused;

        if (Application.Current?.MainWindow is { } window && !ReferenceEquals(window, source)) yield return window;
    }

    private static bool IsRoutable(IInputElement? element)
        => element is UIElement or ContentElement or UIElement3D;
}
