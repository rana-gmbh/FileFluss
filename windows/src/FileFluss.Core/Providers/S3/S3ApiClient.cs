// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// The S3 REST API with SigV4 signing (port of the macOS <c>S3APIClient</c>), shared by AWS
/// S3, generic S3-compatible storage and Synology C2.
///
/// <para><b>Paths.</b> The panel path's first component is the bucket, the rest the key:
/// "/" lists buckets, "/bucket" the bucket's top level, "/bucket/a/b" the prefix "a/b/".
/// Folders are key prefixes; an empty folder is a zero-byte object whose key ends in "/".</para>
///
/// <para><b>Addressing.</b> Always virtual-hosted: <c>&lt;bucket&gt;.s3.&lt;region&gt;.amazonaws.com</c>
/// on AWS, <c>&lt;bucket&gt;.&lt;endpointHost&gt;</c> for custom endpoints. Bucket listing goes to
/// the bare host.</para>
///
/// <para><b>Regions.</b> AWS answers a request sent to the wrong regional endpoint with
/// 301/307 and the bucket's region (header <c>x-amz-bucket-region</c>, or <c>&lt;Region&gt;</c>
/// in the body). The client caches bucket → region and re-signs once. The HttpClient must
/// not follow redirects itself, or the re-signed retry never happens.</para>
/// </summary>
internal sealed class S3ApiClient
{
    private const string Category = "S3";

    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Bucket → region learned from redirects, so later calls skip the bounce.</summary>
    private readonly ConcurrentDictionary<string, string> _bucketRegions = new(StringComparer.Ordinal);

    public S3ApiClient(HttpClient http, S3Credentials credentials, Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        Credentials = credentials;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public S3Credentials Credentials { get; }

    // MARK: - Scope

    /// <summary>
    /// Splits "/bucket/some/folder" into the bucket and the key prefix ("some/folder/"). Null
    /// when no bucket is configured, in which case the panel starts at the bucket list.
    /// </summary>
    public static (string Bucket, string Prefix)? BucketAndPrefix(string? rootPath)
    {
        if (rootPath is null)
        {
            return null;
        }

        var cleaned = rootPath.Trim().Trim('/');
        if (cleaned.Length == 0)
        {
            return null;
        }

        var parts = cleaned.Split('/', 2);
        var bucket = parts[0];
        if (bucket.Length == 0)
        {
            return null;
        }

        var prefix = parts.Length > 1 ? parts[1] : string.Empty;
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        return (bucket, prefix);
    }

    /// <summary>
    /// Connectivity and credentials check. With a bucket configured it lists one key of
    /// <i>that</i> bucket under its prefix rather than the account's buckets: ListBuckets needs
    /// the account-wide s3:ListAllMyBuckets, which a key scoped to one bucket — the ordinary
    /// least-privilege setup — is denied, and policies commonly restrict s3:ListBucket to a
    /// prefix, so probing the bucket root would be refused for a key that can perfectly well
    /// read the folder the user named.
    /// </summary>
    public async Task VerifyAccessAsync(string? rootPath, CancellationToken cancellationToken)
    {
        if (BucketAndPrefix(rootPath) is not { } scope)
        {
            await ListBucketsWithHintAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithResolvedRegionAsync(scope.Bucket, async region =>
        {
            var query = ListQuery(scope.Prefix, delimiter: true, maxKeys: 1, continuationToken: null);
            using var request = BuildRequest(HttpMethod.Get, BucketHost(scope.Bucket, region), "/", query, region, SigV4.EmptyPayloadHash);
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "/" + scope.Bucket, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    // MARK: - Listing

    /// <summary>Lists the items at a panel path (see the class remarks for the mapping).</summary>
    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var cleaned = path.StartsWith('/') ? path[1..] : path;
        if (cleaned.Length == 0)
        {
            // A key scoped to one bucket can't list the account's buckets, so when the user
            // named one, present just that bucket instead of asking for a permission they
            // deliberately didn't grant.
            if (BucketAndPrefix(Credentials.RootPath) is { } scope)
            {
                return [Folder("/" + scope.Bucket, scope.Bucket, "/" + scope.Bucket, CloudFileItem.UnknownDate)];
            }

            return await ListBucketsWithHintAsync(cancellationToken).ConfigureAwait(false);
        }

        var (bucket, key) = Split(cleaned);
        var prefix = key.Length == 0 ? string.Empty : key.EndsWith('/') ? key : key + "/";
        return await ListObjectsAsync(bucket, prefix, "/" + bucket + "/", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudFileItem>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        var host = TopLevelHost(Credentials.Region);
        if (host.Length == 0 || !Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out _))
        {
            throw CloudProviderException.CommandFailed(
                Localization.L("S3 endpoint URL is invalid (host: \"{0}\"). Double-check the Endpoint URL field.", host));
        }

        using var request = BuildRequest(HttpMethod.Get, host, "/", null, Credentials.Region, SigV4.EmptyPayloadHash);
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadSuccessAsync(response, "/", cancellationToken).ConfigureAwait(false);
        return S3Xml.ParseListBuckets(body)
            .Select(b => Folder("/" + b.Name, b.Name, "/" + b.Name, b.CreationDate ?? CloudFileItem.UnknownDate))
            .ToList();
    }

    /// <summary>
    /// Every page of ListObjectsV2 with delimiter "/", as folder rows (CommonPrefixes) then
    /// file rows (Contents). <paramref name="basePath"/> ("/bucket/") prefixes every item path.
    /// </summary>
    public Task<IReadOnlyList<CloudFileItem>> ListObjectsAsync(string bucket, string prefix, string basePath, CancellationToken cancellationToken)
        => WithResolvedRegionAsync<IReadOnlyList<CloudFileItem>>(bucket, async region =>
        {
            var folders = new List<CloudFileItem>();
            var files = new List<CloudFileItem>();
            await foreach (var page in ListPagesAsync(bucket, prefix, delimiter: true, region, cancellationToken).ConfigureAwait(false))
            {
                foreach (var prefixName in page.CommonPrefixes)
                {
                    var folderName = LastPathComponent(StripTrailingSlash(prefixName), prefix);
                    if (folderName.Length == 0)
                    {
                        continue;
                    }

                    folders.Add(Folder(basePath + prefixName, folderName, basePath + StripTrailingSlash(prefixName), CloudFileItem.UnknownDate));
                }

                foreach (var item in page.Contents)
                {
                    // The zero-byte marker that represents the folder itself.
                    if (item.Key == prefix)
                    {
                        continue;
                    }

                    var name = LastPathComponent(item.Key, prefix);
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    files.Add(new CloudFileItem(basePath + item.Key, name, basePath + item.Key, false, item.Size, item.LastModified ?? CloudFileItem.UnknownDate, item.ETag)
                    {
                        IsHidden = CloudFileItem.IsDotHidden(name),
                    });
                }
            }

            return [.. folders, .. files];
        });

    // MARK: - Transfers

    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(remotePath);
        await WithResolvedRegionAsync(bucket, async region =>
        {
            using var request = BuildRequest(HttpMethod.Get, BucketHost(bucket, region), "/" + SigV4.EncodePath(key), null, region, SigV4.EmptyPayloadHash);
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, remotePath, cancellationToken).ConfigureAwait(false);
            await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Single PUT, signed with the file's real SHA-256 (hashed first, streamed). S3 caps a
    /// single PUT at 5 GiB; the provider's <c>MaxUploadFileSize</c> rejects larger files
    /// up front, since multipart upload is not implemented (as on macOS).
    /// </summary>
    public async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(remotePath);
        var size = new FileInfo(localPath).Length;
        var payloadHash = await SigV4.Sha256HexOfFileAsync(localPath, cancellationToken).ConfigureAwait(false);
        var contentType = S3MimeTypes.For(localPath);
        await WithResolvedRegionAsync(bucket, async region =>
        {
            var content = new ProgressStreamContent(HttpTransfer.OpenForUpload(localPath), onBytes, size, contentType);
            using var request = BuildRequest(HttpMethod.Put, BucketHost(bucket, region), "/" + SigV4.EncodePath(key), null, region, payloadHash, content);

            // A wrong-region redirect then arrives before the body is sent rather than after
            // a multi-gigabyte upload (and the progress isn't counted twice on the retry).
            request.Headers.ExpectContinue = true;
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && CloudProviderException.SizeLimitError((int)response.StatusCode, localPath) is { } tooLarge)
            {
                throw tooLarge;
            }

            await EnsureSuccessAsync(response, remotePath, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    // MARK: - Mutations

    public async Task DeleteItemAsync(string remotePath, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(remotePath);
        if (key.Length == 0)
        {
            // Deleting a bucket is deliberately not offered; that belongs in the provider's console.
            throw CloudProviderException.NotImplemented();
        }

        // Folders are prefixes: "deleting" one deletes every object underneath it.
        if (await IsFolderAsync(bucket, key, cancellationToken).ConfigureAwait(false))
        {
            await DeleteFolderAsync(bucket, key.EndsWith('/') ? key : key + "/", cancellationToken).ConfigureAwait(false);
            return;
        }

        await DeleteObjectAsync(bucket, key, remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A zero-byte object whose key ends in "/", so later listings show the empty prefix.</summary>
    public async Task CreateFolderAsync(string remotePath, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(remotePath);
        if (key.Length == 0)
        {
            // Creating buckets is not offered either (macOS would PUT a key named "/").
            throw CloudProviderException.NotImplemented();
        }

        var folderKey = key.EndsWith('/') ? key : key + "/";
        await WithResolvedRegionAsync(bucket, async region =>
        {
            using var request = BuildRequest(HttpMethod.Put, BucketHost(bucket, region), "/" + SigV4.EncodePath(folderKey), null, region, SigV4.EmptyPayloadHash, new ByteArrayContent([]));
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, remotePath, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>Copy to the new key, then delete the old one. Objects only — a prefix ("folder") has no object to copy.</summary>
    public async Task RenameItemAsync(string remotePath, string newName, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(remotePath);
        var slash = key.LastIndexOf('/');
        var newKey = slash < 0 ? newName : key[..(slash + 1)] + newName;
        await CopyObjectAsync(bucket, key, bucket, newKey, remotePath, cancellationToken).ConfigureAwait(false);
        await DeleteObjectAsync(bucket, key, remotePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveItemAsync(string remotePath, string newPath, CancellationToken cancellationToken)
    {
        var (sourceBucket, sourceKey) = SplitPath(remotePath);
        var (destinationBucket, destinationKey) = SplitPath(newPath);
        await CopyObjectAsync(sourceBucket, sourceKey, destinationBucket, destinationKey, remotePath, cancellationToken).ConfigureAwait(false);
        await DeleteObjectAsync(sourceBucket, sourceKey, remotePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyItemAsync(string remotePath, string newPath, CancellationToken cancellationToken)
    {
        var (sourceBucket, sourceKey) = SplitPath(remotePath);
        var (destinationBucket, destinationKey) = SplitPath(newPath);
        await CopyObjectAsync(sourceBucket, sourceKey, destinationBucket, destinationKey, remotePath, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Metadata

    public async Task<CloudFileItem> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(remotePath);
        var name = LastSegment(key.Length == 0 ? bucket : key);
        if (key.Length == 0)
        {
            // The bucket itself. macOS sent a HEAD on the bucket root and read it as a file.
            return Folder(remotePath, bucket, remotePath, CloudFileItem.UnknownDate);
        }

        try
        {
            return await WithResolvedRegionAsync(bucket, async region =>
            {
                using var request = BuildRequest(HttpMethod.Head, BucketHost(bucket, region), "/" + SigV4.EncodePath(key), null, region, SigV4.EmptyPayloadHash);
                using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, remotePath, cancellationToken).ConfigureAwait(false);
                var size = response.Content.Headers.ContentLength ?? 0;
                var etag = response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null;
                var modified = response.Content.Headers.LastModified ?? CloudFileItem.UnknownDate;
                return new CloudFileItem(remotePath, name, remotePath, false, size, modified, etag) { IsHidden = CloudFileItem.IsDotHidden(name) };
            }).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            // No object under that key, but objects beneath it: a folder. Answering NotFound
            // here would make every existence check treat S3 folders as missing.
            if (await IsFolderAsync(bucket, key, cancellationToken).ConfigureAwait(false))
            {
                return Folder(remotePath, name, remotePath, CloudFileItem.UnknownDate);
            }

            throw;
        }
    }

    /// <summary>Sums every object under the prefix (no delimiter). The account root would mean every bucket — too expensive, so 0.</summary>
    public async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken)
    {
        var cleaned = path.StartsWith('/') ? path[1..] : path;
        if (cleaned.Length == 0)
        {
            return 0;
        }

        var (bucket, key) = Split(cleaned);
        var prefix = key.Length > 0 && !key.EndsWith('/') ? key + "/" : key;
        return await WithResolvedRegionAsync(bucket, async region =>
        {
            long total = 0;
            await foreach (var page in ListPagesAsync(bucket, prefix, delimiter: false, region, cancellationToken).ConfigureAwait(false))
            {
                total += page.Contents.Sum(o => o.Size);
            }

            return total;
        }).ConfigureAwait(false);
    }

    // MARK: - Share links (presigned URLs)

    /// <summary>
    /// A presigned GET URL for <paramref name="remotePath"/>, signed locally with the stored
    /// credentials. Deliberately the only S3 sharing mechanism: making an object public would
    /// mean switching off Block Public Access on the user's bucket — a permanent,
    /// account-wide security change an app has no business making, which would also yield a
    /// link with no expiry and no way to revoke it. The link carries the signer's own
    /// authority, so it never outlives the credentials (much sooner than 7 days with STS keys).
    /// </summary>
    public async Task<Uri> PresignedDownloadUrlAsync(string remotePath, TimeSpan expiresIn, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(remotePath);
        if (key.Length == 0)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var seconds = (int)Math.Max(1, Math.Min(Math.Round(expiresIn.TotalSeconds), SigV4.MaximumPresignedExpiry.TotalSeconds));

        // A HEAD warms the bucket → region cache: signing for the wrong region produces a URL
        // that fails only when the recipient opens it. Failures here are ignored.
        try
        {
            await WithResolvedRegionAsync(bucket, async region =>
            {
                using var request = BuildRequest(HttpMethod.Head, BucketHost(bucket, region), "/" + SigV4.EncodePath(key), null, region, SigV4.EmptyPayloadHash);
                using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, remotePath, cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
        }

        var signingRegion = RegionFor(bucket);
        var fileName = LastSegment(key);

        // Served as a download under its own name rather than rendered inline.
        return SigV4.PresignedGetUrl(
            BucketHost(bucket, signingRegion),
            key,
            signingRegion,
            Credentials.AccessKeyId,
            Credentials.SecretAccessKey,
            seconds,
            fileName.Length == 0 ? null : fileName,
            _clock());
    }

    // MARK: - Internals

    private async Task<IReadOnlyList<CloudFileItem>> ListBucketsWithHintAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (IsListBucketsDenial(e))
        {
            // The fix is the Path field, which is easy to miss because it says "optional".
            throw CloudProviderException.CommandFailed(Localization.L(
                "This key isn't allowed to list the account's buckets (s3:ListAllMyBuckets). Enter the bucket name in the Path field — FileFluss then works with that bucket alone and won't ask for account-wide permissions."));
        }
    }

    /// <summary>
    /// macOS only recognised a bare 401/403. AWS always sends an <c>AccessDenied</c> body with
    /// its 403, which surfaced as the raw "AccessDenied: Access Denied" instead of the hint.
    /// </summary>
    private static bool IsListBucketsDenial(CloudProviderException e)
        => e.Kind is CloudErrorKind.Unauthorized or CloudErrorKind.NotAuthenticated
           || (e.Kind == CloudErrorKind.CommandFailed && e.Message.StartsWith("AccessDenied", StringComparison.Ordinal));

    /// <summary>A "folder" either ends in "/" or has objects underneath it.</summary>
    private Task<bool> IsFolderAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var probe = key.EndsWith('/') ? key : key + "/";
        return WithResolvedRegionAsync(bucket, async region =>
        {
            var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", probe), new("max-keys", "1") };
            using var request = BuildRequest(HttpMethod.Get, BucketHost(bucket, region), "/", query, region, SigV4.EmptyPayloadHash);
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await ReadSuccessAsync(response, "/" + bucket + "/" + key, cancellationToken).ConfigureAwait(false);
            var page = S3Xml.ParseListObjectsV2(body);
            return page.Contents.Count > 0 || page.CommonPrefixes.Count > 0;
        });
    }

    /// <summary>
    /// Walks every object under the prefix (no delimiter) and deletes them one at a time.
    /// DeleteObjects could batch this; per-object matches the per-item progress UI.
    /// </summary>
    private async Task DeleteFolderAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        do
        {
            var token = continuationToken;
            var page = await WithResolvedRegionAsync(bucket, async region =>
            {
                using var request = BuildRequest(HttpMethod.Get, BucketHost(bucket, region), "/", ListQuery(prefix, delimiter: false, 1000, token), region, SigV4.EmptyPayloadHash);
                using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
                return S3Xml.ParseListObjectsV2(await ReadSuccessAsync(response, "/" + bucket + "/" + prefix, cancellationToken).ConfigureAwait(false));
            }).ConfigureAwait(false);

            foreach (var item in page.Contents)
            {
                await DeleteObjectAsync(bucket, item.Key, "/" + bucket + "/" + item.Key, cancellationToken).ConfigureAwait(false);
            }

            continuationToken = page.NextContinuationToken == token ? null : page.NextContinuationToken;
        }
        while (continuationToken is not null);
    }

    private Task DeleteObjectAsync(string bucket, string key, string displayPath, CancellationToken cancellationToken)
        => WithResolvedRegionAsync(bucket, async region =>
        {
            using var request = BuildRequest(HttpMethod.Delete, BucketHost(bucket, region), "/" + SigV4.EncodePath(key), null, region, SigV4.EmptyPayloadHash);
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, displayPath, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>
    /// Server-side CopyObject. For a cross-bucket copy both buckets must share a region: the
    /// destination's region signs the request and AWS rejects a mismatch with a clear message.
    /// Objects over 5 GiB would need UploadPartCopy, which macOS doesn't do either.
    /// </summary>
    private Task CopyObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, string displayPath, CancellationToken cancellationToken)
        => WithResolvedRegionAsync(destinationBucket, async region =>
        {
            var copySource = "/" + sourceBucket + "/" + SigV4.EncodePath(sourceKey);
            using var request = BuildRequest(HttpMethod.Put, BucketHost(destinationBucket, region), "/" + SigV4.EncodePath(destinationKey), null, region, SigV4.EmptyPayloadHash, copySource: copySource);

            // Copying a multi-gigabyte object takes minutes; S3 keeps the response open (sending
            // whitespace) until it is done. macOS allowed 600 s per request for the same reason.
            using var response = await _http.SendApiAsync(request, cancellationToken, CopyTimeout).ConfigureAwait(false);
            var body = await ReadSuccessAsync(response, displayPath, cancellationToken).ConfigureAwait(false);

            // A copy that fails after it started still answers 200, with an <Error> body.
            // Treating that as success would let a move delete its only source.
            if (S3Xml.IsErrorDocument(body))
            {
                throw CloudProviderException.CommandFailed(Localization.L("S3 error (HTTP {0}): {1}", 200, S3Xml.ParseError(body) ?? string.Empty));
            }

            return true;
        });

    private async IAsyncEnumerable<ListObjectsV2Page> ListPagesAsync(
        string bucket,
        string prefix,
        bool delimiter,
        string region,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        do
        {
            using var request = BuildRequest(HttpMethod.Get, BucketHost(bucket, region), "/", ListQuery(prefix, delimiter, 1000, continuationToken), region, SigV4.EmptyPayloadHash);
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            var page = S3Xml.ParseListObjectsV2(await ReadSuccessAsync(response, "/" + bucket + "/" + prefix.TrimEnd('/'), cancellationToken).ConfigureAwait(false));
            yield return page;

            // A server that keeps answering with the same token would otherwise loop forever.
            continuationToken = page.NextContinuationToken == continuationToken ? null : page.NextContinuationToken;
        }
        while (continuationToken is not null);
    }

    private static List<KeyValuePair<string, string>> ListQuery(string prefix, bool delimiter, int maxKeys, string? continuationToken)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("list-type", "2"),
            new("prefix", prefix),
            new("max-keys", maxKeys.ToString(CultureInfo.InvariantCulture)),
        };
        if (delimiter)
        {
            query.Add(new("delimiter", "/"));
        }

        if (continuationToken is not null)
        {
            query.Add(new("continuation-token", continuationToken));
        }

        return query;
    }

    /// <summary>Runs <paramref name="operation"/>, retrying once against the region a 301/307 named, and caches it.</summary>
    private async Task<T> WithResolvedRegionAsync<T>(string bucket, Func<string, Task<T>> operation)
    {
        try
        {
            return await operation(RegionFor(bucket)).ConfigureAwait(false);
        }
        catch (RegionRedirectException redirect)
        {
            SupportLogger.Shared.Log($"[S3] Bucket {bucket} lives in {redirect.Region}, retrying", Category);
            _bucketRegions[bucket] = redirect.Region;
            try
            {
                return await operation(redirect.Region).ConfigureAwait(false);
            }
            catch (RegionRedirectException again)
            {
                throw CloudProviderException.ServerError(again.StatusCode);
            }
        }
    }

    private string RegionFor(string bucket) => _bucketRegions.TryGetValue(bucket, out var region) ? region : Credentials.Region;

    /// <summary>The bucket-less host for ListBuckets: the custom endpoint as-is, or <c>s3.&lt;region&gt;.amazonaws.com</c>.</summary>
    internal string TopLevelHost(string region)
        => string.IsNullOrEmpty(Credentials.EndpointHost) ? $"s3.{region}.amazonaws.com" : Credentials.EndpointHost;

    /// <summary>Virtual-hosted bucket host: <c>&lt;bucket&gt;.&lt;endpointHost&gt;</c>, or <c>&lt;bucket&gt;.s3.&lt;region&gt;.amazonaws.com</c>.</summary>
    internal string BucketHost(string bucket, string region)
        => string.IsNullOrEmpty(Credentials.EndpointHost) ? $"{bucket}.s3.{region}.amazonaws.com" : $"{bucket}.{Credentials.EndpointHost}";

    /// <summary>
    /// A signed request. The host is signed — and sent — lower-cased (see
    /// <see cref="SigV4.PresignedGetUrl"/>); the query is sent in its canonical form so the
    /// signed and the transmitted strings are the same. Signed headers: host,
    /// x-amz-content-sha256, x-amz-date, and x-amz-copy-source when copying.
    /// </summary>
    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        string host,
        string encodedPath,
        IEnumerable<KeyValuePair<string, string>>? query,
        string region,
        string payloadHash,
        HttpContent? content = null,
        string? copySource = null)
    {
        var signingHost = host.ToLowerInvariant();
        var canonicalQuery = query is null ? string.Empty : SigV4.CanonicalQuery(query);
        var uri = new Uri("https://" + signingHost + encodedPath + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : string.Empty));
        var now = _clock();
        var amzDate = SigV4.AmzDate(now);

        var signed = new List<KeyValuePair<string, string>>
        {
            new("host", signingHost),
            new("x-amz-content-sha256", payloadHash),
            new("x-amz-date", amzDate),
        };
        if (copySource is not null)
        {
            signed.Add(new("x-amz-copy-source", copySource));
        }

        var authorization = SigV4.Authorization(
            method.Method, encodedPath, canonicalQuery, signed, payloadHash,
            Credentials.AccessKeyId, Credentials.SecretAccessKey, region, "s3", now);

        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Host = signingHost;
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (copySource is not null)
        {
            request.Headers.TryAddWithoutValidation("x-amz-copy-source", copySource);
        }

        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    /// <summary>The body of a successful response; throws like <see cref="EnsureSuccessAsync"/> otherwise.</summary>
    private static async Task<string> ReadSuccessAsync(HttpResponseMessage response, string? path, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws for an unsuccessful response: a region redirect for the retry helper, or the
    /// provider error with the server's own message where the body has one — so "Server
    /// error (HTTP 403)" becomes e.g. "AccessDenied: Signature mismatch."
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string? path, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
        {
            return;
        }

        var body = await response.ReadBodySnippetAsync(cancellationToken, 64 * 1024).ConfigureAwait(false);

        // 301 PermanentRedirect / 307 TemporaryRedirect: the wrong regional endpoint for this
        // bucket. The header is the norm; legacy responses only carry <Region> in the body.
        if (status is 301 or 307)
        {
            var region = response.Headers.TryGetValues("x-amz-bucket-region", out var values) ? values.FirstOrDefault() : null;
            region = string.IsNullOrEmpty(region) ? S3Xml.ParseRegion(body) : region;
            if (!string.IsNullOrEmpty(region))
            {
                throw new RegionRedirectException(region, status);
            }
        }

        var message = S3Xml.ParseError(body);
        SupportLogger.Shared.Log($"[S3] HTTP {status}: {message ?? "<no body>"}", Category, LogLevel.Error);
        throw status switch
        {
            // S3-compatible services send a structured <Error> with most 401/403s.
            401 or 403 => message is not null ? CloudProviderException.CommandFailed(message) : CloudProviderException.Unauthorized(),
            404 => CloudProviderException.NotFound(path ?? message ?? "Not Found"),
            429 or 503 => CloudProviderException.RateLimited(),
            _ => message is not null
                ? CloudProviderException.CommandFailed(Localization.L("S3 error (HTTP {0}): {1}", status, message))
                : CloudProviderException.ServerError(status),
        };
    }

    private static (string Bucket, string Key) SplitPath(string remotePath)
    {
        var cleaned = remotePath.StartsWith('/') ? remotePath[1..] : remotePath;
        if (cleaned.Length == 0)
        {
            throw CloudProviderException.InvalidResponse();
        }

        return Split(cleaned);
    }

    private static (string Bucket, string Key) Split(string cleaned)
    {
        var parts = cleaned.Split('/', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }

    private static string StripTrailingSlash(string value) => value.EndsWith('/') ? value[..^1] : value;

    /// <summary>The name of <paramref name="key"/> below <paramref name="prefix"/>, trailing slash dropped.</summary>
    private static string LastPathComponent(string key, string prefix)
    {
        var trimmed = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
        return LastSegment(StripTrailingSlash(trimmed));
    }

    /// <summary>NSString.lastPathComponent: the part after the last "/", ignoring a trailing one.</summary>
    private static string LastSegment(string value)
    {
        var trimmed = value.Length > 1 ? value.TrimEnd('/') : value;
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private static CloudFileItem Folder(string id, string name, string path, DateTimeOffset date)
        => new(id, name, path, true, 0, date) { IsHidden = CloudFileItem.IsDotHidden(name) };

    /// <summary>"Wrong region, it's over there" — caught by <see cref="WithResolvedRegionAsync{T}"/>, never escapes the client.</summary>
    private sealed class RegionRedirectException(string region, int statusCode) : Exception($"Bucket region is {region}")
    {
        public string Region { get; } = region;

        public int StatusCode { get; } = statusCode;
    }
}
