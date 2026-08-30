using System.Net;
using System.Net.Http;
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
}
