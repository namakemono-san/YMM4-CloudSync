using System.Net;
using System.Net.Http;
using Google;
using Xunit;
using YMM4CloudSync.Core.Commons.Network;

namespace YMM4CloudSync.Tests;

public class CloudErrorsTests
{
    private static GoogleApiException Drive(HttpStatusCode statusCode) =>
        new("drive", "File not found: .") { HttpStatusCode = statusCode };

    [Fact]
    public void DetectsGoogleNotFound()
    {
        Assert.True(CloudErrors.IsNotFound(Drive(HttpStatusCode.NotFound)));
    }

    [Fact]
    public void IgnoresOtherGoogleFailures()
    {
        Assert.False(CloudErrors.IsNotFound(Drive(HttpStatusCode.Forbidden)));
        Assert.False(CloudErrors.IsNotFound(Drive(HttpStatusCode.InternalServerError)));
    }

    [Fact]
    public void DetectsHttpNotFound()
    {
        Assert.True(CloudErrors.IsNotFound(new HttpRequestException("gone", null, HttpStatusCode.NotFound)));
    }

    [Fact]
    public void IgnoresHttpRequestExceptionWithoutStatusCode()
    {
        Assert.False(CloudErrors.IsNotFound(new HttpRequestException("connection reset")));
    }

    [Fact]
    public void UnwrapsNestedCauses()
    {
        var wrapped = new InvalidOperationException("outer",
            new AggregateException(Drive(HttpStatusCode.NotFound)));

        Assert.True(CloudErrors.IsNotFound(wrapped));
    }

    [Fact]
    public void IgnoresUnrelatedExceptions()
    {
        Assert.False(CloudErrors.IsNotFound(new InvalidOperationException("boom")));
        Assert.False(CloudErrors.IsNotFound(null));
    }
}
