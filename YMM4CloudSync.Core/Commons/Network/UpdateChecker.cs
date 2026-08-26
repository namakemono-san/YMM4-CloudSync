using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMM4CloudSync.Core.Commons.Utilities;
using HttpClient = System.Net.Http.HttpClient;

namespace YMM4CloudSync.Core.Commons.Network;

public class UpdateChecker
{
    private const string Owner = "namakemono-san";
    private const string Repo = "YMM4-CloudSync";
    private const string UserAgent = "YMM4CloudSync-UpdateChecker";
    
    private static readonly HttpClient SharedHttpClient = new();

    public async Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            const string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=5";

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            using var response = await SharedHttpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream,
                cancellationToken: cancellationToken);

            if (releases == null || releases.Count == 0) return null;

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

            return SelectUpdate(releases, currentVersion);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            SentryReporter.Capture(ex);
        }

        return null;
    }

    internal static ReleaseInfo? SelectUpdate(IEnumerable<GitHubRelease> releases, Version currentVersion)
    {
        var candidate = releases
            .Where(r => !r.Draft)
            .Select(r => (Release: r, Version: ParseTagVersion(r.TagName)))
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();

        if (candidate.Release == null || candidate.Version is not { } latestVersion) return null;
        if (latestVersion <= currentVersion) return null;

        return new ReleaseInfo
        {
            Version = latestVersion,
            TagName = candidate.Release.TagName,
            Body = candidate.Release.Body,
            HtmlUrl = candidate.Release.HtmlUrl
        };
    }

    private static Version? ParseTagVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        var text = tagName.Trim().TrimStart('v', 'V');

        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0) text = text[..suffixIndex];

        return Version.TryParse(text, out var version) ? version : null;
    }
}

public class ReleaseInfo
{
    public Version Version { get; set; } = new();
    public string? TagName { get; set; }
    public string? Body { get; set; }
    public string? HtmlUrl { get; set; }
}

// ReSharper disable once ClassNeverInstantiated.Global
public record GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("body")]
    public string?  Body { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }
}