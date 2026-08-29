using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Core.Services.WebDav;

public sealed class WebDavClient : IDisposable
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod MkCol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    private const long ChunkThresholdBytes = 16L * 1024 * 1024;
    private const int ChunkSizeBytes = 10 * 1024 * 1024;

    private const string PropFindBody =
        """
        <?xml version="1.0" encoding="utf-8" ?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:resourcetype/>
            <D:getcontentlength/>
            <D:getlastmodified/>
          </D:prop>
        </D:propfind>
        """;

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue? _preemptiveAuthorization;
    private readonly bool _chunkedUploadEnabled;
    private bool _disposed;

    public Uri BaseUri { get; }
    public Uri? UploadsRoot { get; }

    public WebDavClient(Uri baseUri, WebDavSettings settings)
    {
        BaseUri = WebDavResponseParser.EnsureTrailingSlash(baseUri);
        _chunkedUploadEnabled = settings.EnableChunkedUpload;
        UploadsRoot = _chunkedUploadEnabled ? WebDavChunkEndpoint.TryResolveUploadsRoot(BaseUri) : null;

        var handler = new HttpClientHandler { AllowAutoRedirect = true };

        if (settings.AuthMode == WebDavAuthMode.Basic)
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.UserName}:{settings.Password}"));
            _preemptiveAuthorization = new AuthenticationHeaderValue("Basic", raw);
        }
        else
        {
            var credentials = new NetworkCredential(settings.UserName, settings.Password);
            var cache = new CredentialCache { { BaseUri, "Digest", credentials } };

            if (settings.AuthMode == WebDavAuthMode.Automatic)
            {
                cache.Add(BaseUri, "Basic", credentials);
            }

            handler.Credentials = cache;
            handler.PreAuthenticate = true;
        }

        if (settings.AllowUntrustedCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, errors) =>
            {
                if (errors != SslPolicyErrors.None)
                    Debug.WriteLine($"[WebDAV] Ignoring certificate error: {errors}");

                return true;
            };
        }

        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<WebDavResource>> ListAsync(string relativePath, CancellationToken cancellationToken)
    {
        var target = BuildUri(relativePath);

        var xml = await PropFindAsync(target, "1", relativePath, cancellationToken);

        return xml == null ? [] : WebDavResponseParser.ParseMultiStatus(BaseUri, xml);
    }

    public async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendPropFindAsync(BaseUri, "0", cancellationToken);

        await EnsureSuccessAsync(response, "");
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await SendPropFindAsync(BuildUri(relativePath), "0", cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return false;

        await EnsureSuccessAsync(response, relativePath);

        return true;
    }

    public async Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken)
    {
        await CreateDirectoryAsync(BuildUri(relativePath), relativePath, cancellationToken);
    }

    private async Task CreateDirectoryAsync(Uri target, string label, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(MkCol, target);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Created) return;

        await EnsureSuccessAsync(response, label);
    }

    public async Task UploadAsync(string relativePath, string localPath, long length,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (_chunkedUploadEnabled && UploadsRoot != null && length >= ChunkThresholdBytes)
        {
            try
            {
                await UploadChunkedAsync(relativePath, localPath, length, progress, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebDAV] Chunked upload failed, falling back to a single request: {ex.Message}");
            }
        }

        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        await UploadSingleAsync(relativePath, stream, length, progress, cancellationToken);
    }

    private async Task UploadSingleAsync(string relativePath, Stream content, long length,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var origin = content.CanSeek ? content.Position : 0;

        var response = await PutAsync(BuildUri(relativePath), content, length, progress, false, cancellationToken);

        if (ShouldRetryWithExpectContinue(response.StatusCode))
        {
            response.Dispose();

            if (content.CanSeek) content.Seek(origin, SeekOrigin.Begin);

            response = await PutAsync(BuildUri(relativePath), content, length, progress, true, cancellationToken);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, relativePath);
        }
    }

    private async Task UploadChunkedAsync(string relativePath, string localPath, long length,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var transferId = BuildTransferId(relativePath, localPath, length);
        var transferRoot = new Uri(UploadsRoot!, Uri.EscapeDataString(transferId) + "/");

        await CreateDirectoryAsync(transferRoot, transferId, cancellationToken);

        var uploaded = await GetUploadedChunksAsync(transferRoot, cancellationToken);

        await using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var buffer = new byte[ChunkSizeBytes];

            for (long offset = 0; offset < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var size = (int)Math.Min(ChunkSizeBytes, length - offset);
                var name = WebDavChunkEndpoint.BuildChunkName(offset);

                if (uploaded.TryGetValue(name, out var existingSize) && existingSize == size)
                {
                    offset += size;
                    progress?.Report(offset * 100.0 / length);
                    continue;
                }

                stream.Seek(offset, SeekOrigin.Begin);

                var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, size), size, false, cancellationToken);
                if (read == 0) break;

                using var chunk = new MemoryStream(buffer, 0, read, false);

                var chunkUri = new Uri(transferRoot, Uri.EscapeDataString(name));

                using var response = await PutAsync(chunkUri, chunk, read, null, false, cancellationToken);
                await EnsureSuccessAsync(response, name);

                offset += read;
                progress?.Report(offset * 100.0 / length);
            }
        }

        var assembled = new Uri(transferRoot, ".file");

        using var move = CreateRequest(Move, assembled);
        move.Headers.Add("Destination", BuildUri(relativePath).AbsoluteUri);
        move.Headers.Add("Overwrite", "T");

        using var moveResponse = await SendAsync(move, HttpCompletionOption.ResponseContentRead, cancellationToken);

        await EnsureSuccessAsync(moveResponse, relativePath);

        progress?.Report(100.0);
    }

    private async Task<Dictionary<string, long>> GetUploadedChunksAsync(Uri transferRoot, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        try
        {
            using var response = await SendPropFindAsync(transferRoot, "1", cancellationToken);

            if (!response.IsSuccessStatusCode) return result;

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);

            foreach (var resource in WebDavResponseParser.ParseMultiStatus(transferRoot, xml))
            {
                if (resource.IsCollection || resource.ContentLength == null) continue;

                result[resource.Name] = resource.ContentLength.Value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebDAV] Failed to read existing chunks: {ex.Message}");
        }

        return result;
    }

    private static string BuildTransferId(string relativePath, string localPath, long length)
    {
        var stamp = File.GetLastWriteTimeUtc(localPath).Ticks;
        var seed = $"{relativePath}|{length}|{stamp}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));

        return "ymm4cs-" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> PutAsync(Uri target, Stream content, long length,
        IProgress<double>? progress, bool expectContinue, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Put, target);

        var body = new ProgressStreamContent(content, length, progress);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        request.Content = body;
        request.Headers.ExpectContinue = expectContinue;

        return await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public async Task DownloadAsync(string relativePath, Stream destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildUri(relativePath));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        await EnsureSuccessAsync(response, relativePath);

        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[64 * 1024];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            if (total > 0) progress?.Report(received * 100.0 / total);
        }
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, BuildUri(relativePath));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        await EnsureSuccessAsync(response, relativePath);
    }

    public async Task MoveAsync(string sourceRelativePath, string destinationRelativePath,
        bool overwrite, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(Move, BuildUri(sourceRelativePath));
        request.Headers.Add("Destination", BuildUri(destinationRelativePath).AbsoluteUri);
        request.Headers.Add("Overwrite", overwrite ? "T" : "F");

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        await EnsureSuccessAsync(response, sourceRelativePath);
    }

    private async Task<string?> PropFindAsync(Uri target, string depth, string label, CancellationToken cancellationToken)
    {
        using var response = await SendPropFindAsync(target, depth, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await EnsureSuccessAsync(response, label);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendPropFindAsync(Uri target, string depth, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(PropFind, target);
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

        return await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public Uri BuildUri(string relativePath)
    {
        var trimmed = relativePath.Replace('\\', '/').Trim('/');

        if (trimmed.Length == 0) return BaseUri;

        var encoded = string.Join('/', trimmed
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

        return new Uri(BaseUri, encoded);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri target)
    {
        var request = new HttpRequestMessage(method, target);

        if (_preemptiveAuthorization != null)
            request.Headers.Authorization = _preemptiveAuthorization;

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, completionOption, cancellationToken);
        }
        catch (HttpRequestException ex) when (HasCertificateFailure(ex))
        {
            throw new InvalidOperationException(
                "サーバーの証明書が信頼されていません。\n\n" +
                "自己署名証明書を使用している場合は、その証明書を Windows の証明書ストア\n" +
                "（信頼されたルート証明機関）に登録するか、接続設定で\n" +
                "「証明書の検証を無効にする」を有効にしてください。", ex);
        }
    }

    private static bool ShouldRetryWithExpectContinue(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.LengthRequired or HttpStatusCode.ExpectationFailed;
    }

    private static bool HasCertificateFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is AuthenticationException) return true;
        }

        return false;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string relativePath)
    {
        if (response.IsSuccessStatusCode) return;

        var code = (int)response.StatusCode;

        var message = code switch
        {
            401 => "認証に失敗しました。\nユーザー名とパスワードを確認してください。\n\n" +
                   "2要素認証が有効な場合は、通常のパスワードではなく\nアプリパスワードを使用してください。\n" +
                   "サーバーが Digest 認証を要求する場合は、接続設定の認証方式を変更してください。",
            403 => "アクセスが拒否されました。\nこのフォルダへの書き込み権限がない可能性があります。",
            404 => "ファイルまたはフォルダが見つかりませんでした。",
            405 => "この操作はサーバーで許可されていません。\n接続先の URL が WebDAV のエンドポイントか確認してください。",
            409 => "親フォルダが存在しません。\nベースパスの設定を確認してください。",
            423 => "ファイルがロックされています。\n他の端末で編集中の可能性があります。",
            507 => "サーバーの空き容量が不足しています。\n不要なファイルを削除してから再試行してください。",
            >= 500 => "サーバー側の問題で操作できませんでした。\n時間をおいて再試行してください。",
            _ => $"WebDAV の操作に失敗しました。(HTTP {code})"
        };

        var detail = await ReadErrorDetailAsync(response);

        if (!string.IsNullOrWhiteSpace(relativePath))
            message = $"{message}\n\n対象: {relativePath}";

        if (!string.IsNullOrWhiteSpace(detail))
            message = $"{message}\n詳細: {detail}";

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static async Task<string?> ReadErrorDetailAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(body)) return null;

            var condensed = string.Join(' ', body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()));

            return condensed.Length > 300 ? condensed[..300] + "…" : condensed;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _http.Dispose();
    }
}
