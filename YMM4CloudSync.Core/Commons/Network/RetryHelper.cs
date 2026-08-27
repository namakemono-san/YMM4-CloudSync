using System.IO;
using System.Net;
using System.Net.Http;
using Dropbox.Api;
using Google;

namespace YMM4CloudSync.Core.Commons.Network;

public static class RetryHelper
{
    private const int MaxAttempts = 3;
    private static readonly int[] BaseDelayMs = [1000, 2000, 4000];
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        Func<Exception, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        shouldRetry ??= IsTransientError;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < MaxAttempts - 1
                                       && !IsUserCancellation(ex, cancellationToken)
                                       && shouldRetry(ex))
            {
                await Task.Delay(GetDelay(attempt, ex), cancellationToken);
            }
        }
    }

    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        Func<Exception, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, shouldRetry, cancellationToken);
    }

    private static TimeSpan GetDelay(int attempt, Exception ex)
    {
        var retryAfter = GetRetryAfter(ex);
        if (retryAfter != null)
        {
            return retryAfter.Value > MaxRetryAfter ? MaxRetryAfter : retryAfter.Value;
        }

        var index = Math.Min(attempt, BaseDelayMs.Length - 1);
        var baseMs = BaseDelayMs[index];
        var jitter = Random.Shared.Next(0, baseMs / 2);

        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    private static TimeSpan? GetRetryAfter(Exception ex)
    {
        return ex switch
        {
            RateLimitException { RetryAfter: > 0 } rateLimit => TimeSpan.FromSeconds(rateLimit.RetryAfter),
            _ => null
        };
    }

    private static bool IsUserCancellation(Exception ex, CancellationToken cancellationToken)
    {
        return ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }

    private static bool IsTransientError(Exception ex)
    {
        return ex switch
        {
            HttpRequestException httpEx => IsTransientHttpError(httpEx),
            GoogleApiException googleEx => IsTransientStatusCode(googleEx.HttpStatusCode),
            RateLimitException => true,
            RetryException => true,
            TaskCanceledException => true,
            IOException => true,
            _ => false
        };
    }

    private static bool IsTransientHttpError(HttpRequestException ex)
    {
        return ex.StatusCode == null || IsTransientStatusCode(ex.StatusCode.Value);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.Locked => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            HttpStatusCode.InsufficientStorage => false,
            _ => false
        };
    }
}
