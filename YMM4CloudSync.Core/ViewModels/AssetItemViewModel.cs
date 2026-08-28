using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.ViewModels;

public sealed class AssetItemViewModel : INotifyPropertyChanged
{
    public AssetItemViewModel(CloudFile file, string localPath, AssetState state)
    {
        File = file;
        Category = YmmFileTypes.Classify(file.Name, file.IsFolder);
        LocalPath = localPath;
        State = state;
    }

    public CloudFile File { get; }

    public AssetCategory Category { get; }

    public string Id => File.Id;

    public string Name => File.Name;

    public bool IsFolder => File.IsFolder;

    public DateTime? ModifiedTime => File.ModifiedTime;

    public long SizeValue => File.Size ?? -1;

    public string SizeText => IsFolder ? "" : FormatSize(File.Size);

    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(File.Name).TrimStart('.').ToUpperInvariant();

    public string TypeText => Category switch
    {
        AssetCategory.Folder => "フォルダー",
        AssetCategory.Video => Extension.Length > 0 ? $"{Extension} 動画" : "動画",
        AssetCategory.Image => Extension.Length > 0 ? $"{Extension} 画像" : "画像",
        AssetCategory.Audio => Extension.Length > 0 ? $"{Extension} 音声" : "音声",
        AssetCategory.Text => Extension.Length > 0 ? $"{Extension} ファイル" : "テキスト",
        _ => Extension.Length > 0 ? $"{Extension} ファイル" : "ファイル"
    };

    public ImageSource? Icon
    {
        get
        {
            if (field != null || _iconRequested) return field;

            _iconRequested = true;

            _ = LoadIconAsync();

            return field;
        }

        private set => SetProperty(ref field, value);
    }

    private bool _iconRequested;

    private async Task LoadIconAsync()
    {
        var loaded = await ShellIconProvider.GetAsync(
            Name, IsFolder, ShellIconProvider.SizeFor(IconPixels), HasLocalFile ? LocalPath : null);

        if (loaded == null) return;

        Icon = loaded;
    }

    public double IconPixels
    {
        get;
        set
        {
            var changesBucket = ShellIconProvider.SizeFor(field) != ShellIconProvider.SizeFor(value);

            field = value;

            if (!changesBucket) return;

            _iconRequested = false;
            Icon = null;
        }
    } = 16;

    internal CancellationTokenSource? DownloadCts { get; set; }

    public bool IsDragging
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string LocalPath
    {
        get;
        set => SetProperty(ref field, value);
    }

    public AssetState State
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            OnPropertyChanged(nameof(CanDrag));
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateTooltip));
            OnPropertyChanged(nameof(DetailsTooltip));
        }
    }

    public double Progress
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string? ErrorMessage
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            OnPropertyChanged(nameof(StateTooltip));
            OnPropertyChanged(nameof(DetailsTooltip));
        }
    }

    public bool IsDownloading => State == AssetState.Downloading;

    public bool CanDrag => !IsFolder && State is AssetState.Downloaded or AssetState.Stale;

    public bool HasLocalFile => !IsFolder && !string.IsNullOrEmpty(LocalPath) && System.IO.File.Exists(LocalPath);

    public string DetailsTooltip
    {
        get
        {
            var lines = new List<string> { Name, TypeText };

            if (!IsFolder && SizeText.Length > 0) lines.Add($"サイズ: {SizeText}");

            if (ModifiedTime is { } modified) lines.Add($"更新日時: {modified:yyyy/MM/dd HH:mm}");

            if (!IsFolder) lines.Add($"状態: {StateText}");
            if (!IsFolder) lines.Add(StateTooltip);

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string StateText => State switch
    {
        AssetState.NotDownloaded => "未取得",
        AssetState.Downloading => "取得中",
        AssetState.Downloaded => "取得済み",
        AssetState.Stale => "古い",
        AssetState.Failed => "失敗",
        _ => ""
    };

    public string StateTooltip => State switch
    {
        AssetState.NotDownloaded => "クラウドにのみ存在します。ダウンロードするとタイムラインへドラッグできます。",
        AssetState.Downloading => "ダウンロードしています。",
        AssetState.Downloaded => "ドラッグでタイムラインへ配置できます。",
        AssetState.Stale => "クラウド側が更新されています。ドラッグは可能ですが、内容は古いままです。",
        AssetState.Failed => ErrorMessage ?? "ダウンロードに失敗しました。",
        _ => ""
    };

    private static string FormatSize(long? size)
    {
        if (size is not { } bytes) return "";

        string[] units = ["B", "KB", "MB", "GB", "TB"];

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(propertyName);

        return true;
    }
}

public sealed record BreadcrumbNode(string Id, string Name);

public enum AssetSortKey
{
    Name,
    Size,
    Type,
    ModifiedTime,
    State
}

public sealed class AssetItemComparer(AssetSortKey key, bool descending) : System.Collections.IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not AssetItemViewModel a || y is not AssetItemViewModel b) return 0;

        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;

        var result = key switch
        {
            AssetSortKey.Size => a.SizeValue.CompareTo(b.SizeValue),
            AssetSortKey.Type => NaturalOrder.Compare(a.TypeText, b.TypeText),
            AssetSortKey.ModifiedTime => Nullable.Compare(a.ModifiedTime, b.ModifiedTime),
            AssetSortKey.State => ((int)a.State).CompareTo((int)b.State),
            _ => 0
        };

        if (result == 0) result = NaturalOrder.Compare(a.Name, b.Name);

        return descending ? -result : result;
    }
}
