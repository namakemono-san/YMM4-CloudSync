using Xunit;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.Core.Services.WebDav;

namespace YMM4CloudSync.Tests;

public class WebDavChunkEndpointTests
{
    [Theory]
    [InlineData("https://cloud.example.com/remote.php/dav/files/alice/", "/remote.php/dav/uploads/alice/")]
    [InlineData("https://cloud.example.com/remote.php/dav/files/alice", "/remote.php/dav/uploads/alice/")]
    [InlineData("https://host/owncloud/remote.php/dav/files/bob/", "/owncloud/remote.php/dav/uploads/bob/")]
    public void TryResolveUploadsRoot_DerivesTheUploadsEndpoint(string filesRoot, string expectedPath)
    {
        var resolved = WebDavChunkEndpoint.TryResolveUploadsRoot(new Uri(filesRoot));

        Assert.NotNull(resolved);
        Assert.Equal(expectedPath, resolved.AbsolutePath);
    }

    [Theory]
    [InlineData("https://teracloud.jp/dav/")]
    [InlineData("https://example.com/webdav/")]
    [InlineData("https://example.com/")]
    public void TryResolveUploadsRoot_ReturnsNull_ForNonNextcloudLayouts(string filesRoot)
    {
        Assert.Null(WebDavChunkEndpoint.TryResolveUploadsRoot(new Uri(filesRoot)));
    }

    [Fact]
    public void BuildChunkName_SortsLexicographicallyByOffset()
    {
        var names = new[] { 0L, 10_485_760L, 104_857_600L, 1_048_576_000L }
            .Select(WebDavChunkEndpoint.BuildChunkName)
            .ToList();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.All(names, n => Assert.Equal(16, n.Length));
    }
}

public class WebDavConnectionStoreTests
{
    [Fact]
    public void Deserialize_ReadsAListOfConnections()
    {
        const string json = """
            [
              {"id":"a","display_name":"home","server_url":"https://a.example.com/dav/"},
              {"id":"b","display_name":"work","server_url":"https://b.example.com/dav/"}
            ]
            """;

        var connections = WebDavConnectionStore.Deserialize(json);

        Assert.Equal(2, connections.Count);
        Assert.Equal("home", connections[0].DisplayName);
        Assert.Equal("work", connections[1].DisplayName);
    }

    [Fact]
    public void Deserialize_UpgradesTheSingleConnectionFormat()
    {
        const string json = """
            {"server_url":"https://a.example.com/dav/","user_name":"alice","base_path":"YMM4CloudSync"}
            """;

        var connections = WebDavConnectionStore.Deserialize(json);

        Assert.Single(connections);
        Assert.Equal("alice", connections[0].UserName);
        Assert.False(string.IsNullOrWhiteSpace(connections[0].Id));
    }

    [Fact]
    public void Deserialize_ReadsTheAuthModeName()
    {
        const string json = """[{"id":"a","auth_mode":"Digest"}]""";

        Assert.Equal(WebDavAuthMode.Digest, WebDavConnectionStore.Deserialize(json)[0].AuthMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_ReturnsEmpty_ForBlankInput(string json)
    {
        Assert.Empty(WebDavConnectionStore.Deserialize(json));
    }
}

public class WebDavSettingsTests
{
    [Fact]
    public void ResolveDisplayName_PrefersTheExplicitName()
    {
        var settings = new WebDavSettings { DisplayName = " 自宅 ", ServerUrl = "https://a.example.com/dav/" };

        Assert.Equal("自宅", settings.ResolveDisplayName());
    }

    [Fact]
    public void ResolveDisplayName_FallsBackToTheHost()
    {
        var settings = new WebDavSettings { ServerUrl = "https://cloud.example.com/remote.php/dav/files/alice/" };

        Assert.Equal("cloud.example.com", settings.ResolveDisplayName());
    }

    [Fact]
    public void ServiceName_IncludesTheConnectionName()
    {
        var service = new WebDavService(new WebDavSettings { DisplayName = "自宅" });

        Assert.Equal("WebDAV (自宅)", service.ServiceName);
    }

    [Fact]
    public void ValidateAndBuildUri_RejectsPlainHttpByDefault()
    {
        var settings = new WebDavSettings { ServerUrl = "http://example.com/dav/" };

        Assert.Throws<InvalidOperationException>(() => WebDavService.ValidateAndBuildUri(settings));
    }

    [Fact]
    public void ValidateAndBuildUri_AllowsPlainHttpWhenExplicitlyPermitted()
    {
        var settings = new WebDavSettings
        {
            ServerUrl = "http://example.com/dav/",
            AllowInsecureConnection = true
        };

        Assert.Equal("http", WebDavService.ValidateAndBuildUri(settings).Scheme);
    }

    [Theory]
    [InlineData("ftp://example.com/dav/")]
    [InlineData("file:///C:/dav/")]
    [InlineData("not a url")]
    [InlineData("")]
    public void ValidateAndBuildUri_RejectsNonHttpSchemes(string url)
    {
        var settings = new WebDavSettings { ServerUrl = url, AllowInsecureConnection = true };

        Assert.Throws<InvalidOperationException>(() => WebDavService.ValidateAndBuildUri(settings));
    }

    [Fact]
    public void Clone_CopiesEveryField()
    {
        var settings = new WebDavSettings
        {
            Id = "abc",
            DisplayName = "home",
            ServerUrl = "https://a.example.com/dav/",
            UserName = "alice",
            Password = "secret",
            BasePath = "Projects",
            AllowInsecureConnection = true,
            AllowUntrustedCertificate = true,
            AuthMode = WebDavAuthMode.Automatic,
            EnableChunkedUpload = false
        };

        var clone = settings.Clone();

        Assert.Equivalent(settings, clone, strict: true);
        Assert.NotSame(settings, clone);
    }
}
