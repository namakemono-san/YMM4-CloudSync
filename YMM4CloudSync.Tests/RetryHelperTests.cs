using System.Net;
using System.Net.Http;
using Google;
using Xunit;
using YMM4CloudSync.Core.Commons.Network;

namespace YMM4CloudSync.Tests;

public class RetryHelperTests
{
    private static Func<Exception, bool> AlwaysRetry => _ => true;
    private static Func<Exception, bool> NeverRetry => _ => false;

    [Fact]
    public async Task Succeeds_WithoutRetry_WhenOperationSucceeds()
    {
        var attempts = 0;

        var result = await RetryHelper.ExecuteWithRetryAsync(() =>
        {
            attempts++;
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Retries_ExactlyThreeTimes_BeforeGivingUp()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryHelper.ExecuteWithRetryAsync<int>(() =>
            {
                attempts++;
                throw new InvalidOperationException($"attempt {attempts}");
            }, AlwaysRetry));

        Assert.Equal(3, attempts);
        Assert.Equal("attempt 3", ex.Message);
    }

    [Fact]
    public async Task Returns_AsSoonAsAnAttemptSucceeds()
    {
        var attempts = 0;

        var result = await RetryHelper.ExecuteWithRetryAsync(() =>
        {
            attempts++;
            if (attempts < 2) throw new InvalidOperationException();
            return Task.FromResult("ok");
        }, AlwaysRetry);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DoesNotRetry_WhenShouldRetryReturnsFalse()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryHelper.ExecuteWithRetryAsync<int>(() =>
            {
                attempts++;
                throw new InvalidOperationException();
            }, NeverRetry));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DoesNotRetry_WhenCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RetryHelper.ExecuteWithRetryAsync<int>(() =>
            {
                attempts++;
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }, AlwaysRetry, cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Throws_WithoutInvokingOperation_WhenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RetryHelper.ExecuteWithRetryAsync(() =>
            {
                attempts++;
                return Task.FromResult(0);
            }, AlwaysRetry, cts.Token));

        Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task ClassifiesGoogleApiException_ByStatusCode(HttpStatusCode status, bool expectedTransient)
    {
        var attempts = 0;

        await Assert.ThrowsAsync<GoogleApiException>(() =>
            RetryHelper.ExecuteWithRetryAsync<int>(() =>
            {
                attempts++;
                throw new GoogleApiException("drive", "boom") { HttpStatusCode = status };
            }));

        Assert.Equal(expectedTransient ? 3 : 1, attempts);
    }

    [Fact]
    public async Task TreatsHttpRequestExceptionWithoutStatus_AsTransient()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryHelper.ExecuteWithRetryAsync<int>(() =>
            {
                attempts++;
                throw new HttpRequestException("no connection");
            }));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task TreatsNotFound_AsPermanent()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryHelper.ExecuteWithRetryAsync<int>(() =>
            {
                attempts++;
                throw new HttpRequestException("missing", null, HttpStatusCode.NotFound);
            }));

        Assert.Equal(1, attempts);
    }
}
