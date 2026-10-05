// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Web;
using FileFluss.Core.Providers.S3;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// SigV4 pinned against the examples AWS publishes ("Signature Calculations for the
/// Authorization Header: Transferring Payload in a Single Chunk", the presigned-URL example,
/// the signing-key derivation example and the aws-sig-v4-test-suite).
/// </summary>
public class S3SigV4Tests
{
    // AWS's documented example credentials.
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset ExampleDate = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    private static string SignatureOf(string authorization) => authorization[(authorization.IndexOf("Signature=", StringComparison.Ordinal) + "Signature=".Length)..];

    [Fact]
    public void DerivesTheDocumentedSigningKey()
    {
        var key = SigV4.SigningKey("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20120215", "us-east-1", "iam");
        Assert.Equal("f4780e2d9f65fa895f9c67b32ce1baf0b0d8a43505a000a1a9e090d414db404d", Convert.ToHexStringLower(key));
    }

    [Fact]
    public void GetObjectExample()
    {
        var authorization = SigV4.Authorization(
            "GET",
            "/test.txt",
            string.Empty,
            [
                new("Host", "examplebucket.s3.amazonaws.com"),
                new("Range", "bytes=0-9"),
                new("x-amz-content-sha256", SigV4.EmptyPayloadHash),
                new("x-amz-date", "20130524T000000Z"),
            ],
            SigV4.EmptyPayloadHash,
            AccessKey,
            Secret,
            "us-east-1",
            "s3",
            ExampleDate);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            authorization);
    }

    [Fact]
    public void PutObjectExample()
    {
        var payloadHash = SigV4.Sha256Hex("Welcome to Amazon S3.");
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payloadHash);

        // "test$file.text": "$" is not unreserved, so the canonical URI carries %24.
        var canonicalUri = SigV4.EncodePath("/test$file.text");
        Assert.Equal("/test%24file.text", canonicalUri);

        var authorization = SigV4.Authorization(
            "PUT",
            canonicalUri,
            string.Empty,
            [
                new("Host", "examplebucket.s3.amazonaws.com"),
                new("Date", "Fri, 24 May 2013 00:00:00 GMT"),
                new("x-amz-date", "20130524T000000Z"),
                new("x-amz-storage-class", "REDUCED_REDUNDANCY"),
                new("x-amz-content-sha256", payloadHash),
            ],
            payloadHash,
            AccessKey,
            Secret,
            "us-east-1",
            "s3",
            ExampleDate);

        Assert.Contains("SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class", authorization);
        Assert.Equal("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", SignatureOf(authorization));
    }

    [Fact]
    public void GetBucketLifecycleExampleSignsAValuelessParameter()
    {
        var query = SigV4.CanonicalQuery([new("lifecycle", string.Empty)]);
        Assert.Equal("lifecycle=", query);

        var authorization = SigV4.Authorization(
            "GET",
            "/",
            query,
            [
                new("host", "examplebucket.s3.amazonaws.com"),
                new("x-amz-date", "20130524T000000Z"),
                new("x-amz-content-sha256", SigV4.EmptyPayloadHash),
            ],
            SigV4.EmptyPayloadHash,
            AccessKey,
            Secret,
            "us-east-1",
            "s3",
            ExampleDate);

        Assert.Equal("fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543", SignatureOf(authorization));
    }

    [Fact]
    public void ListObjectsExampleSortsTheQuery()
    {
        var query = SigV4.CanonicalQuery([new("prefix", "J"), new("max-keys", "2")]);
        Assert.Equal("max-keys=2&prefix=J", query);

        var authorization = SigV4.Authorization(
            "GET",
            "/",
            query,
            [
                new("host", "examplebucket.s3.amazonaws.com"),
                new("x-amz-date", "20130524T000000Z"),
                new("x-amz-content-sha256", SigV4.EmptyPayloadHash),
            ],
            SigV4.EmptyPayloadHash,
            AccessKey,
            Secret,
            "us-east-1",
            "s3",
            ExampleDate);

        Assert.Equal("34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", SignatureOf(authorization));
    }

    [Fact]
    public void TestSuiteGetVanilla()
    {
        var date = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);
        var authorization = SigV4.Authorization(
            "GET",
            "/",
            string.Empty,
            [new("Host", "example.amazonaws.com"), new("X-Amz-Date", "20150830T123600Z")],
            SigV4.EmptyPayloadHash,
            "AKIDEXAMPLE",
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            "us-east-1",
            "service",
            date);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            authorization);
    }

    [Fact]
    public void CanonicalEncodingFollowsAwsRules()
    {
        // Unreserved stay, everything else (space, "/", "+", "*", non-ASCII) is %XX upper-case.
        Assert.Equal("a%20b%2Fc%2Bd%2A~-_.%C3%A4", SigV4.Encode("a b/c+d*~-_.ä"));
        Assert.Equal("/my%20folder/file%281%29.txt", SigV4.EncodePath("/my folder/file(1).txt"));
        Assert.Equal("a=1&a=2&b=", SigV4.CanonicalQuery([new("b", string.Empty), new("a", "2"), new("a", "1")]));
    }

    // MARK: - Presigned URLs (port of the macOS S3PresignTests)

    [Fact]
    public void PresignMatchesAwsPublishedExample()
    {
        var url = SigV4.PresignedGetUrl("examplebucket.s3.amazonaws.com", "test.txt", "us-east-1", AccessKey, Secret, 86400, null, ExampleDate);
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("examplebucket.s3.amazonaws.com", url.Host);
        Assert.Equal("/test.txt", url.AbsolutePath);
        Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
        Assert.Equal("AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request", query["X-Amz-Credential"]);
        Assert.Equal("20130524T000000Z", query["X-Amz-Date"]);
        Assert.Equal("86400", query["X-Amz-Expires"]);
        Assert.Equal("host", query["X-Amz-SignedHeaders"]);
        Assert.Equal("aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", query["X-Amz-Signature"]);
        Assert.Equal(
            "https://examplebucket.s3.amazonaws.com/test.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            url.AbsoluteUri);
    }

    [Fact]
    public void PresignSignsTheLowercasedHost()
    {
        // Backblaze allows mixed-case bucket names; browsers lower-case the host before sending
        // it. Had the signature covered the mixed-case spelling, the recipient would get
        // SignatureDoesNotMatch.
        static string? Signature(string host)
            => HttpUtility.ParseQueryString(SigV4.PresignedGetUrl(host, "test.txt", "us-east-1", AccessKey, Secret, 86400, null, ExampleDate).Query)["X-Amz-Signature"];

        var mixed = Signature("FileFluss.s3.eu-central-003.backblazeb2.com");
        var lower = Signature("filefluss.s3.eu-central-003.backblazeb2.com");
        Assert.Equal(lower, mixed);
        Assert.False(string.IsNullOrEmpty(mixed));
    }

    [Fact]
    public void PresignSignsTheContentDispositionItAdds()
    {
        static string? Signature(string? fileName)
            => HttpUtility.ParseQueryString(SigV4.PresignedGetUrl("examplebucket.s3.amazonaws.com", "holiday photo.jpg", "eu-central-1", AccessKey, Secret, 3600, fileName, ExampleDate).Query)["X-Amz-Signature"];

        // The disposition parameter is part of the canonical request, so adding it must change
        // the signature — if it didn't, S3 would reject the link as tampered with.
        var withName = Signature("holiday photo.jpg");
        var without = Signature(null);
        Assert.NotEqual(without, withName);
        Assert.False(string.IsNullOrEmpty(withName));
    }

    [Fact]
    public void PresignEncodesTheKeyAndDisposition()
    {
        var url = SigV4.PresignedGetUrl("bucket.s3.eu-central-1.amazonaws.com", "photos/holiday photo.jpg", "eu-central-1", AccessKey, Secret, 3600, "holiday photo.jpg", ExampleDate);
        Assert.StartsWith("https://bucket.s3.eu-central-1.amazonaws.com/photos/holiday%20photo.jpg?", url.AbsoluteUri);
        Assert.Contains("response-content-disposition=attachment%3B%20filename%3D%22holiday%20photo.jpg%22", url.AbsoluteUri);
        Assert.Equal("attachment; filename=\"holiday photo.jpg\"", HttpUtility.ParseQueryString(url.Query)["response-content-disposition"]);
    }
}
