using System.Net;
using System.Net.Http;
using Dropbox.Api;
using Google;

namespace YMM4CloudSync.Core.Commons.Network;

public static class CloudErrors
{
    public static bool IsNotFound(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound }) return true;
            if (current is HttpRequestException { StatusCode: HttpStatusCode.NotFound }) return true;
        }

        return false;
    }

    public static bool IsStorageQuotaExceeded(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is GoogleApiException google && IsGoogleQuota(google)) return true;
            if (current is HttpRequestException { StatusCode: HttpStatusCode.InsufficientStorage }) return true;
            if (current is ApiException<Dropbox.Api.Files.UploadError> upload && IsDropboxQuota(upload)) return true;
            if (current is ApiException<Dropbox.Api.Files.UploadSessionFinishError> finish && IsDropboxQuota(finish)) return true;

            if (ContainsQuotaText(current.Message)) return true;
        }

        return false;
    }

    public static string StorageQuotaMessage(string serviceName)
        => $"{serviceName} の空き容量が不足しているため、アップロードできませんでした。\n" +
           "不要なファイルを削除してごみ箱も空にするか、プランを見直してから再試行してください。";

    private static bool IsGoogleQuota(GoogleApiException exception)
    {
        if (exception.HttpStatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.InsufficientStorage))
            return false;

        var reasons = exception.Error?.Errors;

        if (reasons != null && reasons.Any(e => IsQuotaReason(e.Reason))) return true;

        return ContainsQuotaText(exception.Message);
    }

    private static bool IsQuotaReason(string? reason)
        => reason is "storageQuotaExceeded" or "quotaExceeded";

    private static bool IsDropboxQuota<TError>(ApiException<TError> exception)
        => ContainsQuotaText(exception.ErrorResponse?.ToString());

    private static bool ContainsQuotaText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        return text.Contains("storage quota", StringComparison.OrdinalIgnoreCase)
               || text.Contains("storageQuotaExceeded", StringComparison.OrdinalIgnoreCase)
               || text.Contains("insufficient_space", StringComparison.OrdinalIgnoreCase)
               || text.Contains("insufficient space", StringComparison.OrdinalIgnoreCase);
    }
}
