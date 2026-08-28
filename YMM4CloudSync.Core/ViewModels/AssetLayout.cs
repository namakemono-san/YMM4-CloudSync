using System.ComponentModel;
using System.Runtime.CompilerServices;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Core.ViewModels;

public sealed record AssetLayoutSpec(AssetViewMode Mode, int MinimumIconSize, int MaximumIconSize)
{
    public static readonly int[] SupportedIconSizes = [16, 20, 24, 32, 48, 64, 96, 128];

    public static readonly AssetLayoutSpec List = new(AssetViewMode.List, 16, 20);
    public static readonly AssetLayoutSpec WrapList = new(AssetViewMode.WrapList, 20, 32);
    public static readonly AssetLayoutSpec Tiles = new(AssetViewMode.Tiles, 32, 128);

    public static readonly AssetLayoutSpec[] All = [List, WrapList, Tiles];

    public int[] IconSizes => SupportedIconSizes
        .Where(size => size >= MinimumIconSize && size <= MaximumIconSize)
        .ToArray();
}

public sealed class AssetLayout : INotifyPropertyChanged
{
    public AssetViewMode Mode
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            RaiseDerived();
        }
    } = AssetViewMode.List;

    public int IconSize
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            RaiseDerived();
        }
    } = 16;

    public bool IsList => Mode == AssetViewMode.List;

    public bool IsWrapList => Mode == AssetViewMode.WrapList;

    public bool IsTiles => Mode == AssetViewMode.Tiles;

    public double ItemWidth => Mode switch
    {
        AssetViewMode.WrapList => IconSize + 4 + 180,
        AssetViewMode.Tiles => Math.Max(56, IconSize + 16),
        _ => double.NaN
    };

    public double ItemHeight => Mode switch
    {
        AssetViewMode.Tiles => ItemWidth + TextHeight,
        _ => Math.Max(18, IconSize + 2)
    };

    public double TextHeight => Mode == AssetViewMode.Tiles ? 34 : 17;

    public bool CanDecrease
    {
        get
        {
            var (specIndex, index, _) = Steps();

            return index > 0 || specIndex > 0;
        }
    }

    public bool CanIncrease
    {
        get
        {
            var (specIndex, index, sizes) = Steps();

            return index < sizes.Length - 1 || specIndex < AssetLayoutSpec.All.Length - 1;
        }
    }

    public void Decrease()
    {
        var (specIndex, index, sizes) = Steps();

        if (index > 0)
        {
            IconSize = sizes[index - 1];
            return;
        }

        if (specIndex <= 0) return;

        var spec = AssetLayoutSpec.All[specIndex - 1];

        Mode = spec.Mode;
        IconSize = spec.MaximumIconSize;
    }

    public void Increase()
    {
        var (specIndex, index, sizes) = Steps();

        if (index < sizes.Length - 1)
        {
            IconSize = sizes[index + 1];
            return;
        }

        if (specIndex >= AssetLayoutSpec.All.Length - 1) return;

        var spec = AssetLayoutSpec.All[specIndex + 1];

        Mode = spec.Mode;
        IconSize = spec.MinimumIconSize;
    }

    public void SwitchTo(AssetViewMode mode)
    {
        var spec = AssetLayoutSpec.All.First(s => s.Mode == mode);

        Mode = mode;
        IconSize = Math.Clamp(IconSize, spec.MinimumIconSize, spec.MaximumIconSize);
    }

    private (int specIndex, int index, int[] sizes) Steps()
    {
        var spec = AssetLayoutSpec.All.FirstOrDefault(s => s.Mode == Mode) ?? AssetLayoutSpec.List;
        var sizes = spec.IconSizes;
        var index = Array.IndexOf(sizes, IconSize);

        if (index < 0) index = 0;

        return (Array.IndexOf(AssetLayoutSpec.All, spec), index, sizes);
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsList));
        OnPropertyChanged(nameof(IsWrapList));
        OnPropertyChanged(nameof(IsTiles));
        OnPropertyChanged(nameof(ItemWidth));
        OnPropertyChanged(nameof(ItemHeight));
        OnPropertyChanged(nameof(TextHeight));
        OnPropertyChanged(nameof(CanDecrease));
        OnPropertyChanged(nameof(CanIncrease));
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
