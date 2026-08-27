using System.Globalization;
using System.Xml.Linq;

namespace YMM4CloudSync.Core.Services.WebDav;

public static class WebDavResponseParser
{
    private static readonly XNamespace Dav = "DAV:";

    public static IReadOnlyList<WebDavResource> ParseMultiStatus(Uri baseUri, string xml)
    {
        var document = XDocument.Parse(xml);
        var results = new List<WebDavResource>();

        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;

            var relativePath = NormalizeHref(baseUri, href);
            if (relativePath == null || relativePath.Length == 0) continue;

            var properties = FindSuccessfulProperties(response);
            if (properties == null) continue;

            var isCollection = properties
                .Element(Dav + "resourcetype")?
                .Element(Dav + "collection") != null;

            results.Add(new WebDavResource(
                relativePath,
                GetName(relativePath),
                isCollection,
                isCollection ? null : ParseContentLength(properties),
                ParseLastModified(properties)));
        }

        return results;
    }

    public static string? NormalizeHref(Uri baseUri, string href)
    {
        var normalizedBase = EnsureTrailingSlash(baseUri);

        Uri resolved;

        try
        {
            resolved = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                ? absolute
                : new Uri(normalizedBase, href);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var basePath = normalizedBase.AbsolutePath;
        var targetPath = resolved.AbsolutePath;

        if (!targetPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = targetPath[basePath.Length..];

        return Uri.UnescapeDataString(relative).Trim('/');
    }

    public static Uri EnsureTrailingSlash(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith('/')) return uri;

        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath + "/" };

        return builder.Uri;
    }

    private static XElement? FindSuccessfulProperties(XContainer response)
    {
        XElement? fallback = null;

        foreach (var propstat in response.Elements(Dav + "propstat"))
        {
            var prop = propstat.Element(Dav + "prop");
            if (prop == null) continue;

            var status = propstat.Element(Dav + "status")?.Value ?? "";

            if (status.Contains(" 200", StringComparison.Ordinal)) return prop;

            fallback ??= prop;
        }

        return response.Element(Dav + "prop") ?? fallback;
    }

    private static string GetName(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');

        return lastSlash >= 0 ? relativePath[(lastSlash + 1)..] : relativePath;
    }

    private static long? ParseContentLength(XContainer properties)
    {
        var raw = properties.Element(Dav + "getcontentlength")?.Value;

        if (string.IsNullOrWhiteSpace(raw)) return null;

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
    }

    private static readonly string[] DateFormats =
    [
        "r",
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "dd MMM yyyy HH:mm:ss 'GMT'",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ"
    ];

    internal static DateTime? ParseHttpDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();

        if (TryParseDate(value, out var parsed)) return parsed;

        var comma = value.IndexOf(',');

        if (comma >= 0 && comma + 1 < value.Length && TryParseDate(value[(comma + 1)..].Trim(), out var withoutWeekday))
        {
            return withoutWeekday;
        }

        return null;
    }

    private static bool TryParseDate(string value, out DateTime result)
    {
        if (DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var exact))
        {
            result = exact.ToLocalTime().DateTime;
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var general))
        {
            result = general.ToLocalTime().DateTime;
            return true;
        }

        result = default;
        return false;
    }

    private static DateTime? ParseLastModified(XContainer properties)
    {
        return ParseHttpDate(properties.Element(Dav + "getlastmodified")?.Value);
    }
}
