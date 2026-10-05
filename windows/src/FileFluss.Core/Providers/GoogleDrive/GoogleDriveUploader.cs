// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GoogleDrive;

/// <summary>
/// Drive v3 uploads, shared by both Google providers: a multipart upload up to 5 MB, a
/// resumable session in 10 MB chunks above that. Streams from disk — the macOS client read
/// the whole file into memory first.
/// </summary>
internal sealed class GoogleDriveUploader
{
    public const long SimpleUploadLimit = 5_000_000;

    /// <summary>Must be a multiple of 256 KiB, Google's resumable-upload granularity.</summary>
    public const int ChunkSize = 10 * 1024 * 1024;

    private const int MaxNotFoundRetries = 3;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _accessToken;
    private readonly Func<int, string, CloudProviderException> _mapError;
    private readonly string _logCategory;

    /// <param name="mapError">Turns a failed status and its body into the provider's own error.</param>
    public GoogleDriveUploader(HttpClient http, Func<CancellationToken, Task<string>> accessToken, Func<int, string, CloudProviderException> mapError, string logCategory)
    {
        _http = http;
        _accessToken = accessToken;
        _mapError = mapError;
        _logCategory = logCategory;
    }

    /// <summary>
    /// Retry a multipart upload answered 404: Drive's upload service occasionally doesn't yet
    /// see a just-created parent folder (see the full provider's create-folder poll).
    /// </summary>
    public bool RetryNotFound { get; init; }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Creates <paramref name="fileName"/> in <paramref name="parentId"/>, or replaces the
    /// content of <paramref name="existingId"/>. A new file gets the timestamps in its
    /// metadata; a replacement can't (see <see cref="Endpoint"/>), so the caller stamps them.
    /// </summary>
    public async Task<GoogleDriveFile> UploadAsync(
        string localPath,
        string fileName,
        string parentId,
        string? existingId,
        DateTimeOffset? modified,
        DateTimeOffset? created,
        ByteProgress? onBytes,
        CancellationToken cancellationToken)
    {
        var size = new FileInfo(localPath).Length;
        return size <= SimpleUploadLimit
            ? await SimpleUploadAsync(localPath, fileName, parentId, existingId, modified, created, onBytes, cancellationToken).ConfigureAwait(false)
            : await ResumableUploadAsync(localPath, size, fileName, parentId, existingId, modified, created, onBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// POST creates a new file; PATCH replaces the content of an existing one. <c>fields=</c>
    /// makes the response carry id/name/mimeType. Drive rejects modifiedTime/createdTime on an
    /// upload-endpoint PATCH with HTTP 403, so a replacement sends empty metadata.
    /// </summary>
    private static (HttpMethod Method, string Url, string Metadata) Endpoint(
        string uploadType, string fileName, string parentId, string? existingId, DateTimeOffset? modified, DateTimeOffset? created)
        => existingId is not null
            ? (HttpMethod.Patch,
               $"{GoogleDriveApi.UploadBase}/files/{CloudHttp.Escape(existingId)}?uploadType={uploadType}&fields=id,name,mimeType&supportsAllDrives=true",
               GoogleDriveApi.BuildMetadata(null, null, null, null))
            : (HttpMethod.Post,
               $"{GoogleDriveApi.UploadBase}/files?uploadType={uploadType}&fields=id,name,mimeType&supportsAllDrives=true",
               GoogleDriveApi.BuildMetadata(fileName, parentId, modified, created));

    private async Task<GoogleDriveFile> SimpleUploadAsync(
        string localPath, string fileName, string parentId, string? existingId, DateTimeOffset? modified, DateTimeOffset? created, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var (method, url, metadata) = Endpoint("multipart", fileName, parentId, existingId, modified, created);
        var progress = new HighWaterProgress(onBytes);

        for (var attempt = 0; ; attempt++)
        {
            progress.Restart();
            var accessToken = await _accessToken(cancellationToken).ConfigureAwait(false);
            var boundary = Guid.NewGuid().ToString().ToUpperInvariant();
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = CloudHttp.Bearer(accessToken);

            var content = new MultipartContent("related", boundary)
            {
                new StringContent(metadata, Encoding.UTF8, "application/json"),
                ProgressStreamContent.FromFile(localPath, progress.Callback),
            };

            // MultipartContent quotes the boundary; send it bare, exactly as macOS does.
            content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");
            request.Content = content;

            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return Decode(body);
            }

            var status = (int)response.StatusCode;
            SupportLogger.Shared.Log(
                $"Upload failed: HTTP {status} (parentId={parentId}, existingId={existingId ?? "none"}, attempt={attempt + 1}): {Snippet(body)}",
                _logCategory,
                LogLevel.Error);
            if (RetryNotFound && status == 404 && attempt < MaxNotFoundRetries)
            {
                await Task.Delay(RetryDelay * (attempt + 1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw _mapError(status, body);
        }
    }

    private async Task<GoogleDriveFile> ResumableUploadAsync(
        string localPath, long size, string fileName, string parentId, string? existingId, DateTimeOffset? modified, DateTimeOffset? created, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var (method, url, metadata) = Endpoint("resumable", fileName, parentId, existingId, modified, created);
        var accessToken = await _accessToken(cancellationToken).ConfigureAwait(false);

        // Step 1: open the session; its URL comes back in Location.
        Uri sessionUri;
        using (var init = new HttpRequestMessage(method, url))
        {
            init.Headers.Authorization = CloudHttp.Bearer(accessToken);
            init.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
            init.Headers.TryAddWithoutValidation("X-Upload-Content-Length", size.ToString(CultureInfo.InvariantCulture));
            init.Content = new StringContent(metadata, Encoding.UTF8, "application/json");

            using var response = await _http.SendApiAsync(init, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
                SupportLogger.Shared.Log($"Resumable upload init failed: HTTP {(int)response.StatusCode}: {body}", _logCategory, LogLevel.Error);
                throw _mapError((int)response.StatusCode, body);
            }

            var location = response.Headers.Location ?? throw CloudProviderException.InvalidResponse();
            sessionUri = location.IsAbsoluteUri ? location : new Uri(new Uri(url), location);
        }

        // Step 2: the bytes, chunk by chunk. Earlier chunks are answered 308 Resume
        // Incomplete; the final chunk's 200/201 carries the file JSON.
        string? finalBody = null;
        for (long offset = 0; offset < size;)
        {
            var end = Math.Min(offset + ChunkSize, size);
            using var put = new HttpRequestMessage(HttpMethod.Put, sessionUri);
            var chunk = new ProgressStreamContent(new FileSliceStream(localPath, offset, end - offset), onBytes);
            chunk.Headers.ContentRange = new ContentRangeHeaderValue(offset, end - 1, size);
            put.Content = chunk;

            using var response = await _http.SendTransferAsync(put, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                finalBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (status != 308)
            {
                var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
                SupportLogger.Shared.Log($"Resumable chunk {offset}-{end - 1} failed: HTTP {status}: {body}", _logCategory, LogLevel.Error);
                throw _mapError(status, body);
            }

            offset = end;
        }

        return Decode(finalBody ?? throw CloudProviderException.InvalidResponse());
    }

    private static GoogleDriveFile Decode(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<GoogleDriveFile>(body, CloudHttp.Json) ?? throw CloudProviderException.InvalidResponse();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static string Snippet(string body) => body.Length <= 500 ? body : body[..500];
}
