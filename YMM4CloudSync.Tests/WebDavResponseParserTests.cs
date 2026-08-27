using Xunit;
using YMM4CloudSync.Core.Services.WebDav;

namespace YMM4CloudSync.Tests;

public class WebDavResponseParserTests
{
    private static readonly Uri NextcloudBase =
        new("https://cloud.example.com/remote.php/dav/files/alice/");

    [Theory]
    [InlineData("/remote.php/dav/files/alice/YMM4CloudSync/a.ymmx", "YMM4CloudSync/a.ymmx")]
    [InlineData("https://cloud.example.com/remote.php/dav/files/alice/YMM4CloudSync/a.ymmx", "YMM4CloudSync/a.ymmx")]
    [InlineData("/remote.php/dav/files/alice/YMM4CloudSync/", "YMM4CloudSync")]
    [InlineData("/remote.php/dav/files/alice/", "")]
    public void NormalizeHref_HandlesAbsoluteAndRelativeForms(string href, string expected)
    {
        Assert.Equal(expected, WebDavResponseParser.NormalizeHref(NextcloudBase, href));
    }

    [Theory]
    [InlineData("/remote.php/dav/files/alice/%E3%83%86%E3%82%B9%E3%83%88.ymmx", "テスト.ymmx")]
    [InlineData("/remote.php/dav/files/alice/a%20b/c%23d.ymmx", "a b/c#d.ymmx")]
    public void NormalizeHref_DecodesPercentEncoding(string href, string expected)
    {
        Assert.Equal(expected, WebDavResponseParser.NormalizeHref(NextcloudBase, href));
    }

    [Fact]
    public void NormalizeHref_ReturnsNull_ForHrefOutsideTheBase()
    {
        Assert.Null(WebDavResponseParser.NormalizeHref(NextcloudBase, "/remote.php/dav/files/bob/secret.ymmx"));
    }

    [Fact]
    public void EnsureTrailingSlash_AddsSlashWhenMissing()
    {
        var uri = WebDavResponseParser.EnsureTrailingSlash(new Uri("https://example.com/dav"));

        Assert.Equal("/dav/", uri.AbsolutePath);
    }

    private const string MultiStatusXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/remote.php/dav/files/alice/YMM4CloudSync/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/></d:resourcetype>
                <d:getlastmodified>Wed, 26 Aug 2026 10:00:00 GMT</d:getlastmodified>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/alice/YMM4CloudSync/%E5%8B%95%E7%94%BB.ymmx</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype/>
                <d:getcontentlength>1234567</d:getcontentlength>
                <d:getlastmodified>Thu, 27 Aug 2026 01:23:45 GMT</d:getlastmodified>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
            <d:propstat>
              <d:prop><d:quota-used-bytes/></d:prop>
              <d:status>HTTP/1.1 404 Not Found</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/alice/YMM4CloudSync/sub/</d:href>
            <d:propstat>
              <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public void ParseMultiStatus_ReadsEveryEntry()
    {
        var resources = WebDavResponseParser.ParseMultiStatus(NextcloudBase, MultiStatusXml);

        Assert.Equal(3, resources.Count);
    }

    [Fact]
    public void ParseMultiStatus_StripsTrailingSlashFromCollections()
    {
        var resources = WebDavResponseParser.ParseMultiStatus(NextcloudBase, MultiStatusXml);

        var collection = resources.Single(r => r.RelativePath == "YMM4CloudSync");

        Assert.True(collection.IsCollection);
        Assert.Equal("YMM4CloudSync", collection.Name);
    }

    [Fact]
    public void ParseMultiStatus_ReadsFileMetadata()
    {
        var resources = WebDavResponseParser.ParseMultiStatus(NextcloudBase, MultiStatusXml);

        var file = resources.Single(r => r.Name == "動画.ymmx");

        Assert.False(file.IsCollection);
        Assert.Equal(1234567, file.ContentLength);
        Assert.Equal("YMM4CloudSync/動画.ymmx", file.RelativePath);
        Assert.NotNull(file.LastModified);
    }

    [Fact]
    public void ParseMultiStatus_IgnoresPropertiesFromFailedPropstat()
    {
        var resources = WebDavResponseParser.ParseMultiStatus(NextcloudBase, MultiStatusXml);

        var file = resources.Single(r => r.Name == "動画.ymmx");

        Assert.Equal(1234567, file.ContentLength);
    }

    [Fact]
    public void ParseMultiStatus_LeavesCollectionSizeUnset()
    {
        var resources = WebDavResponseParser.ParseMultiStatus(NextcloudBase, MultiStatusXml);

        Assert.All(
            resources.Where(r => r.IsCollection),
            r => Assert.Null(r.ContentLength));
    }

    [Fact]
    public void ParseMultiStatus_HandlesUppercaseNamespacePrefixes()
    {
        const string xml = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/remote.php/dav/files/alice/x.ymmx</D:href>
                <D:propstat>
                  <D:prop><D:resourcetype/><D:getcontentlength>10</D:getcontentlength></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """;

        var resources = WebDavResponseParser.ParseMultiStatus(NextcloudBase, xml);

        Assert.Single(resources);
        Assert.Equal("x.ymmx", resources[0].Name);
    }

    [Fact]
    public void ParseMultiStatus_SkipsTheBaseItself()
    {
        const string xml = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/dav/files/alice/</d:href>
                <d:propstat>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        Assert.Empty(WebDavResponseParser.ParseMultiStatus(NextcloudBase, xml));
    }

    [Theory]
    [InlineData("Thu, 27 Aug 2026 01:23:45 GMT")]
    [InlineData("Mon, 27 Aug 2026 01:23:45 GMT")]
    [InlineData("27 Aug 2026 01:23:45 GMT")]
    [InlineData("2026-08-27T01:23:45Z")]
    public void ParseHttpDate_AcceptsCommonServerFormats(string raw)
    {
        var parsed = WebDavResponseParser.ParseHttpDate(raw);

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, 8, 27), parsed.Value.ToUniversalTime().Date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void ParseHttpDate_ReturnsNull_ForUnusableValues(string? raw)
    {
        Assert.Null(WebDavResponseParser.ParseHttpDate(raw));
    }
}
