using System.Text.RegularExpressions;

namespace YMM4CloudSync.Core.Services.WebDav;

public static partial class WebDavChunkEndpoint
{
    [GeneratedRegex(@"^(?<prefix>.*/dav)/files/(?<user>[^/]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex FilesEndpoint { get; }

    public static Uri? TryResolveUploadsRoot(Uri filesRoot)
    {
        var match = FilesEndpoint.Match(filesRoot.AbsolutePath.TrimEnd('/'));

        if (!match.Success) return null;

        var path = $"{match.Groups["prefix"].Value}/uploads/{match.Groups["user"].Value}/";

        return new UriBuilder(filesRoot) { Path = path, Query = "", Fragment = "" }.Uri;
    }

    public static string BuildChunkName(long offset) => offset.ToString("D16");
}
