using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Xunit;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Tests;

public class SentryFilterTests
{
    [Fact]
    public void ReportsOrdinaryExceptions()
    {
        Assert.True(SentryFilter.ShouldReport(new InvalidOperationException("boom")));
        Assert.True(SentryFilter.ShouldReport(new NullReferenceException()));
    }

    [Fact]
    public void DropsNull()
    {
        Assert.False(SentryFilter.ShouldReport(null));
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    public void DropsUserCancellation(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.False(SentryFilter.ShouldReport(exception));
    }

    [Fact]
    public void DropsDnsAndConnectivityFailures()
    {
        var dns = new HttpRequestException("no such host", new SocketException(11001));

        Assert.True(SentryFilter.IsUnactionableNetworkError(dns));
        Assert.False(SentryFilter.ShouldReport(dns));
    }

    [Fact]
    public void DropsHttpRequestExceptionWithoutStatusCode()
    {
        Assert.False(SentryFilter.ShouldReport(new HttpRequestException("connection reset")));
    }

    [Fact]
    public void KeepsHttpRequestExceptionWithStatusCode()
    {
        var server = new HttpRequestException("boom", null, HttpStatusCode.InternalServerError);

        Assert.True(SentryFilter.ShouldReport(server));
    }

    [Fact]
    public void DropsSocketExceptionNestedDeeply()
    {
        var wrapped = new InvalidOperationException("outer",
            new IOException("io", new SocketException(10054)));

        Assert.False(SentryFilter.ShouldReport(wrapped));
    }

    [Fact]
    public void KeepsIoExceptionThatIsNotNetworkRelated()
    {
        var locked = new IOException("The process cannot access the file because it is being used by another process.");

        Assert.True(SentryFilter.ShouldReport(locked));
    }

    [Fact]
    public void KeepsFileTooLongError()
    {
        Assert.True(SentryFilter.ShouldReport(new IOException("The file is too long.")));
    }

    [Fact]
    public void RecognisesOwnFrames()
    {
        string?[] modules =
        [
            "YukkuriMovieMaker",
            "PresentationFramework",
            "YMM4CloudSync.Core"
        ];

        Assert.True(SentryFilter.HasOwnFrames(modules));
    }

    [Fact]
    public void RejectsFramesFromOtherAssembliesOnly()
    {
        string?[] modules =
        [
            "YukkuriMovieMaker",
            "PresentationFramework",
            "YukkuriMovieMaker.Plugin.Community",
            null
        ];

        Assert.False(SentryFilter.HasOwnFrames(modules));
    }

    [Fact]
    public void RejectsEmptyFrameList()
    {
        Assert.False(SentryFilter.HasOwnFrames([]));
    }
}
