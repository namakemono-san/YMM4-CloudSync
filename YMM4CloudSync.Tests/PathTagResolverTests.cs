using System.IO;
using Xunit;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Tests;

public class PathTagResolverTests
{
    private const string YmmUserDir = @"D:\YMM4\user";

    [Fact]
    public void Resolve_ExpandsYmmUserDirTag_FromSuppliedDirectory()
    {
        var result = PathTagResolver.Resolve(@"<YMMUserDir>\projects", null, YmmUserDir);

        Assert.Equal(@"D:\YMM4\user\projects", result);
    }

    [Fact]
    public void Resolve_ExpandsProjectDirTag_ThatItselfUsesYmmUserDir()
    {
        var result = PathTagResolver.Resolve(@"<ProjectDir>\cache", @"<YMMUserDir>\p", YmmUserDir);

        Assert.Equal(@"D:\YMM4\user\p\cache", result);
    }

    [Fact]
    public void Resolve_ExpandsDesktopTag()
    {
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        Assert.Equal(expected, PathTagResolver.Resolve("<Desktop>", null, YmmUserDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ReturnsEmpty_ForBlankInput(string? input)
    {
        Assert.Equal("", PathTagResolver.Resolve(input, null, YmmUserDir));
    }

    [Fact]
    public void Resolve_LeavesUnknownTagsUntouched()
    {
        Assert.Equal(@"<Unknown>\x", PathTagResolver.Resolve(@"<Unknown>\x", null, YmmUserDir));
    }

    [Fact]
    public void ResolveProjectDirectory_CollapsesSelfReferenceToDefault()
    {
        Assert.Equal(
            PathTagResolver.DefaultProjectDirectory,
            PathTagResolver.ResolveProjectDirectory("<ProjectDir>", YmmUserDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveProjectDirectory_FallsBackToDefault(string? input)
    {
        Assert.Equal(
            PathTagResolver.DefaultProjectDirectory,
            PathTagResolver.ResolveProjectDirectory(input, YmmUserDir));
    }

    [Fact]
    public void CombineWithin_KeepsResultUnderTheBaseDirectory()
    {
        Assert.Equal(
            @"D:\cache\evil.ymmx",
            PathTagResolver.CombineWithin(@"D:\cache", @"..\..\evil.ymmx", "fallback.ymmx"));
    }

    [Fact]
    public void CombineWithin_Segments_BuildsAHierarchy()
    {
        Assert.Equal(
            @"D:\assets\google-drive\背景\森\朝.png",
            PathTagResolver.CombineWithin(@"D:\assets", ["google-drive", "背景", "森", "朝.png"], "asset"));
    }

    [Fact]
    public void CombineWithin_Segments_ReturnsTheBase_ForAnEmptyList()
    {
        Assert.Equal(@"D:\assets", PathTagResolver.CombineWithin(@"D:\assets", [], "asset"));
    }

    [Fact]
    public void CombineWithin_Segments_SanitisesEverySegment()
    {
        Assert.Equal(
            @"D:\assets\a_b\c_d.png",
            PathTagResolver.CombineWithin(@"D:\assets", ["a:b", "c?d.png"], "asset"));
    }

    [Fact]
    public void CombineWithin_Segments_KeepsAnAbsoluteSegmentInside()
    {
        var result = PathTagResolver.CombineWithin(@"D:\assets", [@"C:\windows", "a.png"], "asset");

        Assert.StartsWith(@"D:\assets\", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CombineWithin_Segments_FallsBackWhenASegmentIsTraversal()
    {
        Assert.Equal(
            @"D:\assets\asset\a.png",
            PathTagResolver.CombineWithin(@"D:\assets", ["..", "a.png"], "asset"));
    }

    [Fact]
    public void DefaultAssetDirectory_IsNotUnderTemp()
    {
        Assert.DoesNotContain(
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(PathTagResolver.DefaultAssetDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultAssetDirectory_IsSeparateFromProjects()
    {
        Assert.NotEqual(PathTagResolver.DefaultProjectDirectory, PathTagResolver.DefaultAssetDirectory);
    }

    [Fact]
    public void DefaultCacheDirectory_IsUnderTemp()
    {
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(PathTagResolver.DefaultCacheDirectory),
            StringComparison.OrdinalIgnoreCase);
    }
}
