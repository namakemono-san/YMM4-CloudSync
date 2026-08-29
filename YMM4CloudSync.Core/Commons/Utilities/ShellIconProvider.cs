using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class ShellIconProvider
{
    private const string FolderKey = "<folder>";

    private static readonly BlockingCollection<Request> Queue = new(new ConcurrentQueue<Request>());

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<bool> Worker = new(StartWorkers, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly record struct Request(string Key, string Probe, bool IsFolder, ShellIcon.IconSize Size,
        TaskCompletionSource<ImageSource?> Completion);

    public static ShellIcon.IconSize SizeFor(double pixels) => pixels switch
    {
        <= 16 => ShellIcon.IconSize.Small16,
        <= 32 => ShellIcon.IconSize.Large32,
        <= 48 => ShellIcon.IconSize.ExtraLarge48,
        _ => ShellIcon.IconSize.Jumbo256
    };

    public static Task<ImageSource?> GetAsync(string fileName, bool isFolder, ShellIcon.IconSize size,
        string? existingPath = null)
    {
        var key = BuildKey(fileName, isFolder, size);

        if (Cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);

        if (!Worker.Value) return Task.FromResult<ImageSource?>(null);

        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = !string.IsNullOrEmpty(existingPath) && File.Exists(existingPath)
            ? existingPath
            : BuildProbePath(fileName, isFolder);

        try
        {
            Queue.Add(new Request(key, probe, isFolder, size, completion));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AssetTab] Shell icon request failed: {ex.Message}");
            return Task.FromResult<ImageSource?>(null);
        }

        return completion.Task;
    }

    private static string BuildKey(string fileName, bool isFolder, ShellIcon.IconSize size)
    {
        if (isFolder) return $"{FolderKey}|{size}";

        var extension = Path.GetExtension(fileName);

        return string.IsNullOrEmpty(extension) ? $"<none>|{size}" : $"{extension}|{size}";
    }

    private static string BuildProbePath(string fileName, bool isFolder)
    {
        if (isFolder) return Path.GetTempPath();

        var extension = Path.GetExtension(fileName);

        return Path.Combine(Path.GetTempPath(), "ymm4cloudsync_icon" + extension);
    }

    private static bool StartWorkers()
    {
        try
        {
            var count = Math.Max(1, Environment.ProcessorCount / 4);

            for (var i = 0; i < count; i++)
            {
                var thread = new Thread(Work) { IsBackground = true, Name = "YMM4CS.ShellIcon" };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AssetTab] Shell icon workers unavailable: {ex.Message}");
            return false;
        }
    }

    private static void Work()
    {
        foreach (var request in Queue.GetConsumingEnumerable())
        {
            if (Cache.TryGetValue(request.Key, out var cached))
            {
                request.Completion.TrySetResult(cached);
                continue;
            }

            ImageSource? icon = null;

            try
            {
                icon = ShellIcon.GetIcon(request.Probe, request.Size, request.IsFolder);

                if (icon is { CanFreeze: true }) icon.Freeze();
                else icon = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AssetTab] Shell icon lookup failed for {request.Key}: {ex.Message}");
                icon = null;
            }

            Cache[request.Key] = icon;
            request.Completion.TrySetResult(icon);
        }
    }
}
