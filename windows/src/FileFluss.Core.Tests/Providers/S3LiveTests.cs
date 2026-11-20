// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.S3;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// End-to-end signing against a real S3-compatible server. Skipped unless
/// FILEFLUSS_LIVE_S3_ENDPOINT, FILEFLUSS_LIVE_S3_ACCESS_KEY and FILEFLUSS_LIVE_S3_SECRET_KEY are
/// set (FILEFLUSS_LIVE_S3_REGION defaults to us-east-1). MinIO's public playground,
/// https://play.min.io, works with the demo keys MinIO publishes. It serves path-style only,
/// so the provider check goes through ListBuckets (the bare host) and the object round trip
/// drives the signer directly.
/// </summary>
public sealed class S3LiveTests
{
    private sealed record Settings(string Endpoint, string Host, string AccessKey, string SecretKey, string Region);

    private static Settings? Live()
    {
        var endpoint = Environment.GetEnvironmentVariable("FILEFLUSS_LIVE_S3_ENDPOINT");
        var accessKey = Environment.GetEnvironmentVariable("FILEFLUSS_LIVE_S3_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("FILEFLUSS_LIVE_S3_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            return null;
        }

        var region = Environment.GetEnvironmentVariable("FILEFLUSS_LIVE_S3_REGION");
        return new Settings(endpoint, S3CompatibleProvider.HostFromEndpoint(endpoint), accessKey, secretKey, string.IsNullOrWhiteSpace(region) ? "us-east-1" : region);
    }

    public sealed class LiveS3FactAttribute : FactAttribute
    {
        public LiveS3FactAttribute()
        {
            if (Live() is null)
            {
                Skip = "Live S3 check: set FILEFLUSS_LIVE_S3_ENDPOINT, FILEFLUSS_LIVE_S3_ACCESS_KEY and FILEFLUSS_LIVE_S3_SECRET_KEY to run.";
            }
        }
    }

    [LiveS3Fact]
    public async Task ProviderSignsInAndListsBuckets()
    {
        var settings = Live()!;
        using var provider = new S3CompatibleProvider(Guid.NewGuid(), new MemoryCredentialStore());

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string>
            {
                ["accessKeyId"] = settings.AccessKey,
                ["secretAccessKey"] = settings.SecretKey,
                ["endpoint"] = settings.Endpoint,
                ["region"] = settings.Region,
            }),
            new ScriptedConnectPrompt());

        Assert.True(provider.IsAuthenticated);
        Assert.NotNull(result.DisplayName);
        Assert.All(await provider.ListDirectoryAsync("/"), b => Assert.True(b.IsDirectory));
    }

    [LiveS3Fact]
    public async Task SignedPutAndPresignedGetRoundTrip()
    {
        var settings = Live()!;
        using var http = new HttpClient();
        var bucket = "filefluss-live-" + Guid.NewGuid().ToString("N")[..12];
        var key = $"{bucket}/folder/hello world+(1).txt";
        var body = Encoding.UTF8.GetBytes("Hello from the FileFluss SigV4 live test.");

        using (var create = await SendAsync(http, settings, HttpMethod.Put, "/" + bucket, null))
        {
            Assert.True(create.IsSuccessStatusCode, $"create bucket: HTTP {(int)create.StatusCode} {await create.Content.ReadAsStringAsync()}");
        }

        try
        {
            using (var put = await SendAsync(http, settings, HttpMethod.Put, "/" + key, body))
            {
                Assert.True(put.IsSuccessStatusCode, $"put object: HTTP {(int)put.StatusCode} {await put.Content.ReadAsStringAsync()}");
            }

            // Path-style presign: the "key" is bucket/key on the bare host.
            var url = SigV4.PresignedGetUrl(settings.Host, key, settings.Region, settings.AccessKey, settings.SecretKey, 300, "hello world+(1).txt", DateTimeOffset.UtcNow);
            using var anonymous = new HttpClient();
            using var get = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal(body, await get.Content.ReadAsByteArrayAsync());
            Assert.Contains("attachment", get.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);
        }
        finally
        {
            using var deleteObject = await SendAsync(http, settings, HttpMethod.Delete, "/" + key, null);
            using var deleteBucket = await SendAsync(http, settings, HttpMethod.Delete, "/" + bucket, null);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, Settings settings, HttpMethod method, string path, byte[]? body)
    {
        var payloadHash = body is null ? SigV4.EmptyPayloadHash : Convert.ToHexStringLower(SHA256.HashData(body));
        var now = DateTimeOffset.UtcNow;
        var amzDate = SigV4.AmzDate(now);
        var canonicalUri = SigV4.EncodePath(path);
        var host = settings.Host.ToLowerInvariant();
        var authorization = SigV4.Authorization(
            method.Method, canonicalUri, string.Empty,
            [new("host", host), new("x-amz-content-sha256", payloadHash), new("x-amz-date", amzDate)],
            payloadHash, settings.AccessKey, settings.SecretKey, settings.Region, "s3", now);

        using var request = new HttpRequestMessage(method, new Uri("https://" + host + canonicalUri));
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        return await http.SendAsync(request);
    }
}
