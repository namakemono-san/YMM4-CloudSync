using Xunit;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.ViewModels;

namespace YMM4CloudSync.Tests;

public class AssetLayoutTests
{
    [Fact]
    public void StartsAsACompactList()
    {
        var layout = new AssetLayout();

        Assert.Equal(AssetViewMode.List, layout.Mode);
        Assert.Equal(16, layout.IconSize);
        Assert.True(layout.IsList);
    }

    [Fact]
    public void CannotShrinkBelowTheSmallestList()
    {
        var layout = new AssetLayout();

        Assert.False(layout.CanDecrease);

        layout.Decrease();

        Assert.Equal(AssetViewMode.List, layout.Mode);
        Assert.Equal(16, layout.IconSize);
    }

    [Fact]
    public void IncreasingWalksThroughEveryMode()
    {
        var layout = new AssetLayout();
        var modes = new List<AssetViewMode> { layout.Mode };

        for (var i = 0; i < 40 && layout.CanIncrease; i++)
        {
            layout.Increase();

            if (modes[^1] != layout.Mode) modes.Add(layout.Mode);
        }

        Assert.Equal([AssetViewMode.List, AssetViewMode.WrapList, AssetViewMode.Tiles], modes);
        Assert.Equal(128, layout.IconSize);
        Assert.False(layout.CanIncrease);
    }

    [Fact]
    public void IncreasingThenDecreasingReturnsToTheStart()
    {
        var layout = new AssetLayout();

        for (var i = 0; i < 40 && layout.CanIncrease; i++) layout.Increase();
        for (var i = 0; i < 40 && layout.CanDecrease; i++) layout.Decrease();

        Assert.Equal(AssetViewMode.List, layout.Mode);
        Assert.Equal(16, layout.IconSize);
    }

    [Fact]
    public void SwitchingModeClampsTheIconSize()
    {
        var layout = new AssetLayout { IconSize = 16 };

        layout.SwitchTo(AssetViewMode.Tiles);

        Assert.Equal(AssetViewMode.Tiles, layout.Mode);
        Assert.Equal(32, layout.IconSize);

        layout.SwitchTo(AssetViewMode.List);

        Assert.Equal(20, layout.IconSize);
    }

    [Fact]
    public void ListModeHasNoFixedItemWidth()
    {
        var layout = new AssetLayout();

        Assert.True(double.IsNaN(layout.ItemWidth));
    }

    [Fact]
    public void WrapAndTileModesHaveAFixedItemWidth()
    {
        var layout = new AssetLayout();

        layout.SwitchTo(AssetViewMode.WrapList);
        Assert.False(double.IsNaN(layout.ItemWidth));

        layout.SwitchTo(AssetViewMode.Tiles);
        Assert.False(double.IsNaN(layout.ItemWidth));
    }

    [Fact]
    public void ChangingTheModeRaisesTheDerivedProperties()
    {
        var layout = new AssetLayout();
        var changed = new List<string?>();

        layout.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        layout.SwitchTo(AssetViewMode.Tiles);

        Assert.Contains(nameof(AssetLayout.IsTiles), changed);
        Assert.Contains(nameof(AssetLayout.ItemWidth), changed);
        Assert.Contains(nameof(AssetLayout.ItemHeight), changed);
    }

    [Fact]
    public void EveryModeExposesAtLeastOneIconSize()
    {
        Assert.All(AssetLayoutSpec.All, spec => Assert.NotEmpty(spec.IconSizes));
    }
}
