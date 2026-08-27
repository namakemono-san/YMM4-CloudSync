using System.Text.Json.Serialization;

namespace YMM4CloudSync.Core.Models;

public enum WebDavAuthMode
{
    Basic,
    Digest,
    Automatic
}

public class WebDavSettings
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("user_name")]
    public string UserName { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("base_path")]
    public string BasePath { get; set; } = "YMM4CloudSync";

    [JsonPropertyName("allow_insecure_connection")]
    public bool AllowInsecureConnection { get; set; }

    [JsonPropertyName("allow_untrusted_certificate")]
    public bool AllowUntrustedCertificate { get; set; }

    [JsonPropertyName("auth_mode")]
    public WebDavAuthMode AuthMode { get; set; } = WebDavAuthMode.Basic;

    [JsonPropertyName("enable_chunked_upload")]
    public bool EnableChunkedUpload { get; set; } = true;

    public string ResolveDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName.Trim();

        return Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ? uri.Host : "未設定";
    }

    public WebDavSettings Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ServerUrl = ServerUrl,
        UserName = UserName,
        Password = Password,
        BasePath = BasePath,
        AllowInsecureConnection = AllowInsecureConnection,
        AllowUntrustedCertificate = AllowUntrustedCertificate,
        AuthMode = AuthMode,
        EnableChunkedUpload = EnableChunkedUpload
    };
}
