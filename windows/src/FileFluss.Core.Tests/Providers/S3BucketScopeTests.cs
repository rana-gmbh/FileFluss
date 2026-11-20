// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using FileFluss.Core.Providers.S3;
using FileFluss.Core.Security;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// A key scoped to one bucket has no s3:ListAllMyBuckets, so connecting must never depend on
/// listing the account's buckets when the user named one (GitHub issue #53). Port of the
/// macOS S3BucketScopeTests.
/// </summary>
public class S3BucketScopeTests
{
    [Fact]
    public void AConfiguredPathYieldsTheBucketAndItsPrefix()
    {
        var plain = S3ApiClient.BucketAndPrefix("/my-bucket");
        Assert.Equal("my-bucket", plain?.Bucket);
        Assert.Equal(string.Empty, plain?.Prefix);

        var nested = S3ApiClient.BucketAndPrefix("/my-bucket/team/reports");
        Assert.Equal("my-bucket", nested?.Bucket);

        // The trailing slash matters: S3 treats "team/reports" and "team/reports/" as different prefixes.
        Assert.Equal("team/reports/", nested?.Prefix);

        // However the user typed it.
        Assert.Equal("my-bucket", S3ApiClient.BucketAndPrefix("my-bucket/")?.Bucket);
        Assert.Equal("my-bucket", S3ApiClient.BucketAndPrefix("  my-bucket  ")?.Bucket);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("   ")]
    public void NoPathMeansNoBucketScope(string? path) => Assert.Null(S3ApiClient.BucketAndPrefix(path));

    [Fact]
    public void CredentialsCarryTheScopeAndOlderEntriesDecodeWithoutOne()
    {
        var store = new MemoryCredentialStore();
        store.Save("s3.scoped", new S3Credentials("AKIA…", "secret", "eu-north-1", "AWS S3", RootPath: "/my-bucket/team"));
        var decoded = store.Load<S3Credentials>("s3.scoped");
        Assert.Equal("my-bucket", S3ApiClient.BucketAndPrefix(decoded?.RootPath)?.Bucket);

        // An account stored before bucket scoping existed has no such key; it must keep
        // working and keep its old behaviour. Same JSON as the macOS keychain entry.
        store.Save("s3.legacy", Encoding.UTF8.GetBytes(
            """{"accessKeyId":"AKIA…","secretAccessKey":"secret","region":"eu-north-1","displayName":"AWS S3"}"""));
        var legacy = store.Load<S3Credentials>("s3.legacy");
        Assert.NotNull(legacy);
        Assert.Equal("eu-north-1", legacy.Region);
        Assert.Null(legacy.RootPath);
        Assert.Null(legacy.EndpointHost);
        Assert.Null(S3ApiClient.BucketAndPrefix(legacy.RootPath));
    }

    [Fact]
    public void StoredJsonUsesTheMacKeys()
    {
        var store = new MemoryCredentialStore();
        store.Save("k", new S3Credentials("AK", "SK", "fsn1", "Hetzner", "fsn1.your-objectstorage.com", "/b"));
        var json = Encoding.UTF8.GetString(store.Load("k")!);
        foreach (var key in new[] { "accessKeyId", "secretAccessKey", "region", "displayName", "endpointHost", "rootPath" })
        {
            Assert.Contains($"\"{key}\":", json);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" / ", null)]
    [InlineData("my-bucket", "/my-bucket")]
    [InlineData(" /my-bucket/team/ ", "/my-bucket/team")]
    public void RootPathNormalization(string? typed, string? expected)
        => Assert.Equal(expected, S3FamilyProvider.NormalizeRootPath(typed));
}
