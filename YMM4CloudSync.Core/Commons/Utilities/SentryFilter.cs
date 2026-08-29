using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class SentryFilter
{
    public const string OwnAssemblyPrefix = "YMM4CloudSync";

    public static bool ShouldReport(Exception? exception)
    {
        if (exception == null) return false;

        if (IsUserCancellation(exception)) return false;

        if (exception is CloudNotAuthenticatedException) return false;

        return !IsUnactionableNetworkError(exception);
    }

    public static bool IsUserCancellation(Exception exception)
        => exception is OperationCanceledException;

    public static bool IsUnactionableNetworkError(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SocketException) return true;

            if (current is HttpRequestException http && http.StatusCode == null) return true;

            if (current is IOException io && io.InnerException is SocketException) return true;
        }

        return false;
    }

    public static bool HasOwnFrames(IEnumerable<string?> moduleNames)
    {
        return moduleNames.Any(module =>
            module != null && module.Contains(OwnAssemblyPrefix, StringComparison.Ordinal));
    }
}
