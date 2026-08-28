using Xunit;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Tests;

public class AssetFileTypesTests
{
    [Theory]
    [InlineData("clip.mp4", AssetCategory.Video)]
    [InlineData("clip.MKV", AssetCategory.Video)]
    [InlineData("scene.mov", AssetCategory.Video)]
    [InlineData("背景.png", AssetCategory.Image)]
    [InlineData("photo.JPG", AssetCategory.Image)]
    [InlineData("layered.psd", AssetCategory.Image)]
    [InlineData("voice.wav", AssetCategory.Audio)]
    [InlineData("bgm.Mp3", AssetCategory.Audio)]
    [InlineData("se.flac", AssetCategory.Audio)]
    [InlineData("notes.txt", AssetCategory.Text)]
    [InlineData("timeline.exo", AssetCategory.Text)]
    [InlineData("project.ymmx", AssetCategory.Text)]
    [InlineData("archive.zip", AssetCategory.Other)]
    [InlineData("tool.exe", AssetCategory.Other)]
    public void Classify_MapsExtensionToCategory(string fileName, AssetCategory expected)
    {
        Assert.Equal(expected, AssetFileTypes.Classify(fileName, false));
    }

    [Fact]
    public void Classify_ReturnsFolder_WhenTheEntryIsAFolder()
    {
        Assert.Equal(AssetCategory.Folder, AssetFileTypes.Classify("素材.mp4", true));
    }

    [Fact]
    public void Classify_ReturnsOther_WhenThereIsNoExtension()
    {
        Assert.Equal(AssetCategory.Other, AssetFileTypes.Classify("README", false));
    }

    [Fact]
    public void Classify_UsesTheLastExtension()
    {
        Assert.Equal(AssetCategory.Image, AssetFileTypes.Classify("movie.mp4.png", false));
    }

    [Fact]
    public void Classify_HandlesJapaneseNames()
    {
        Assert.Equal(AssetCategory.Audio, AssetFileTypes.Classify("ゆっくり 実況 音声.wav", false));
    }
}
