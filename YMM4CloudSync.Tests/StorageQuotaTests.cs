using System.Net;
using System.Net.Http;
using Google;
using Google.Apis.Requests;
using Xunit;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Tests;

public class StorageQuotaTests
{
    private static GoogleApiException Drive(HttpStatusCode status, string? reason, string message)
    {
        var exception = new GoogleApiException("drive", message) { HttpStatusCode = status };

        if (reason != null)
        {
            exception.Error = new RequestError
            {
                Message = message,
                Errors = [new SingleError { Reason = reason, Message = message }]
            };
        }

        return exception;
    }

    [Fact]
    public void DetectsGoogleStorageQuotaByReason()
    {
        var exception = Drive(HttpStatusCode.Forbidden, "storageQuotaExceeded",
            "The user's Drive storage quota has been exceeded.");

        Assert.True(CloudErrors.IsStorageQuotaExceeded(exception));
    }

    [Fact]
    public void DetectsGoogleStorageQuotaByMessage()
    {
        var exception = Drive(HttpStatusCode.Forbidden, null,
            "The service drive has thrown an exception. HttpStatusCode is Forbidden. " +
            "The user's Drive storage quota has been exceeded.");

        Assert.True(CloudErrors.IsStorageQuotaExceeded(exception));
    }

    [Fact]
    public void IgnoresUnrelatedGoogleForbidden()
    {
        Assert.False(CloudErrors.IsStorageQuotaExceeded(
            Drive(HttpStatusCode.Forbidden, "insufficientFilePermissions", "Insufficient permissions.")));
    }

    [Fact]
    public void DetectsInsufficientStorageStatus()
    {
        Assert.True(CloudErrors.IsStorageQuotaExceeded(
            new HttpRequestException("full", null, HttpStatusCode.InsufficientStorage)));
    }

    [Fact]
    public void DetectsDropboxInsufficientSpaceText()
    {
        Assert.True(CloudErrors.IsStorageQuotaExceeded(
            new Exception("path/insufficient_space/...")));
    }

    [Fact]
    public void UnwrapsNestedCauses()
    {
        var wrapped = new InvalidOperationException("upload failed",
            Drive(HttpStatusCode.Forbidden, "storageQuotaExceeded", "quota"));

        Assert.True(CloudErrors.IsStorageQuotaExceeded(wrapped));
    }

    [Fact]
    public void IgnoresUnrelatedExceptions()
    {
        Assert.False(CloudErrors.IsStorageQuotaExceeded(new InvalidOperationException("boom")));
        Assert.False(CloudErrors.IsStorageQuotaExceeded(null));
    }

    [Fact]
    public void MessageNamesTheService()
    {
        var message = CloudErrors.StorageQuotaMessage("Google ドライブ");

        Assert.Contains("Google ドライブ", message, StringComparison.Ordinal);
        Assert.Contains("空き容量", message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaFailuresAreNotReportedToSentry()
    {
        Assert.False(SentryFilter.ShouldReport(new CloudStorageFullException("full")));

        Assert.False(SentryFilter.ShouldReport(
            Drive(HttpStatusCode.Forbidden, "storageQuotaExceeded", "quota")));
    }

    [Fact]
    public void OrdinaryFailuresAreStillReported()
    {
        Assert.True(SentryFilter.ShouldReport(new InvalidOperationException("boom")));
    }
}
