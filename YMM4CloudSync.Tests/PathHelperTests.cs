using System.IO;
using Xunit;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Tests;

public class PathHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePath_ReturnsEmpty_ForBlankInput(string? input)
    {
        Assert.Equal("", PathHelper.ResolvePath(input));
    }

    [Fact]
    public void ResolvePath_ExpandsDesktopTag()
    {
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        Assert.Equal(expected, PathHelper.ResolvePath("<Desktop>"));
    }

    [Fact]
    public void ResolvePath_ExpandsDocumentsTag()
    {
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Assert.Equal(Path.Combine(expected, "sub"), PathHelper.ResolvePath(@"<Documents>\sub"));
    }

    [Fact]
    public void ResolvePath_ExpandsProjectDirTag_FromSuppliedProjectDirectory()
    {
        var result = PathHelper.ResolvePath(@"<ProjectDir>\cache", @"D:\Projects");

        Assert.Equal(@"D:\Projects\cache", result);
    }

    [Fact]
    public void ResolvePath_ExpandsProjectDirTag_ToDefault_WhenProjectDirectoryIsBlank()
    {
        var result = PathHelper.ResolvePath("<ProjectDir>");

        Assert.Equal(PathHelper.DefaultProjectDirectory, result);
    }

    [Fact]
    public void ResolvePath_LeavesUnknownTagsUntouched()
    {
        Assert.Equal(@"<Unknown>\x", PathHelper.ResolvePath(@"<Unknown>\x"));
    }

    [Fact]
    public void ResolveProjectDirectory_DoesNotRecurse_OnSelfReference()
    {
        Assert.Equal(PathHelper.DefaultProjectDirectory, PathHelper.ResolveProjectDirectory("<ProjectDir>"));
    }

    [Fact]
    public void ResolveProjectDirectory_FallsBackToDefault_WhenBlank()
    {
        Assert.Equal(PathHelper.DefaultProjectDirectory, PathHelper.ResolveProjectDirectory(""));
    }

    [Theory]
    [InlineData(@"..\..\evil.ymmx", "evil.ymmx")]
    [InlineData("../../evil.ymmx", "evil.ymmx")]
    [InlineData(@"C:\Windows\System32\evil.ymmx", "evil.ymmx")]
    [InlineData(@"sub\dir\file.ymmx", "file.ymmx")]
    [InlineData("nor:mal.ymmx", "nor_mal.ymmx")]
    [InlineData("a<b>c.ymmx", "a_b_c.ymmx")]
    public void SanitizeFileName_ReducesToASingleSafeSegment(string input, string expected)
    {
        Assert.Equal(expected, PathHelper.SanitizeFileName(input, "fallback"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("   ")]
    [InlineData(@"..\..")]
    public void SanitizeFileName_UsesFallback_ForUnusableNames(string? input)
    {
        Assert.Equal("fallback", PathHelper.SanitizeFileName(input, "fallback"));
    }

    [Fact]
    public void SanitizeFileName_StripsTrailingDots()
    {
        Assert.Equal("name", PathHelper.SanitizeFileName("name...", "fallback"));
    }

    [Fact]
    public void CombineWithin_KeepsResultUnderTheBaseDirectory()
    {
        var result = PathHelper.CombineWithin(@"D:\cache", @"..\..\evil.ymmx", "fallback.ymmx");

        Assert.Equal(@"D:\cache\evil.ymmx", result);
    }

    [Fact]
    public void CombineWithin_UsesFallback_WhenNameIsUnusable()
    {
        var result = PathHelper.CombineWithin(@"D:\cache", "..", "fallback.ymmx");

        Assert.Equal(@"D:\cache\fallback.ymmx", result);
    }

    [Fact]
    public void CombineWithin_IgnoresAbsolutePathsInTheName()
    {
        var result = PathHelper.CombineWithin(@"D:\cache", @"C:\Windows\evil.ymmx", "fallback.ymmx");

        Assert.Equal(@"D:\cache\evil.ymmx", result);
    }
}
