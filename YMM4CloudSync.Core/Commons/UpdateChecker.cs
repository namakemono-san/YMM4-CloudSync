using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HttpClient = System.Net.Http.HttpClient;

namespace YMM4CloudSync.Core.Commons;

public class UpdateChecker
{
    private const string Owner = "namakemono-san";
    private const string Repo = "YMM4-CloudSync";
    private const string UserAgent = "YMM4CloudSync-UpdateChecker";

    public async Task<ReleaseInfo?> CheckForUpdatesAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            const string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases";
            var response = await client.GetAsync(new Uri(url));

            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream);

            if (releases == null || releases.Count == 0) return null;

            var latest = releases.FirstOrDefault(r => !r.Prerelease);
            if (latest == null) return null;

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
            var tagVersionStr = latest.TagName?.TrimStart('v') ?? "0.1.0";

            if (Version.TryParse(tagVersionStr, out var latestVersion))
            {
                if (latestVersion > currentVersion)
                {
                    return new ReleaseInfo
                    {
                        Version = latestVersion,
                        TagName = latest.TagName,
                        Body = latest.Body,
                        HtmlUrl = latest.HtmlUrl
                    };
                }
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
        }

        return null;
    }
}

public class ReleaseInfo
{
    public Version Version { get; set; } = new();
    public string? TagName { get; set; }
    public string? Body { get; set; }
    public string? HtmlUrl { get; set; }
}

public abstract class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }
}