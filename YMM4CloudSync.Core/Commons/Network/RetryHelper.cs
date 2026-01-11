using System.IO;
using System.Net;
using System.Net.Http;

namespace YMM4CloudSync.Core.Commons.Network;

public static class RetryHelper
{
    private const int MaxRetries = 3;
    private static readonly int[] DelayMs = [1000, 2000, 4000];

    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        Func<Exception, bool>? shouldRetry = null)
    {
        shouldRetry ??= IsTransientError;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < MaxRetries - 1 && shouldRetry(ex))
            {
                await Task.Delay(DelayMs[attempt]);
            }
        }

        // Final attempt without catching exceptions
        return await operation();
    }

    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        Func<Exception, bool>? shouldRetry = null)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, shouldRetry);
    }

    private static bool IsTransientError(Exception ex)
    {
        return ex switch
        {
            HttpRequestException httpEx => IsTransientHttpError(httpEx),
            TaskCanceledException => true,
            IOException => true,
            _ => false
        };
    }

    private static bool IsTransientHttpError(HttpRequestException ex)
    {
        if (ex.StatusCode == null) return true;

        return ex.StatusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false
        };
    }
}
