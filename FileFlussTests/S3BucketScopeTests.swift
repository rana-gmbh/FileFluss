import Testing
import Foundation
import FileFlussCore

/// A key scoped to one bucket has no `s3:ListAllMyBuckets`, so connecting
/// must never depend on listing the account's buckets when the user named
/// one (GitHub issue #53).
@Suite("S3 bucket scoping")
struct S3BucketScopeTests {

    @Test("a configured path yields the bucket and its prefix")
    func parsesBucketAndPrefix() {
        let plain = S3APIClient.bucketAndPrefix(from: "/my-bucket")
        #expect(plain?.bucket == "my-bucket")
        #expect(plain?.prefix == "")

        let nested = S3APIClient.bucketAndPrefix(from: "/my-bucket/team/reports")
        #expect(nested?.bucket == "my-bucket")
        // Trailing slash matters: S3 treats "team/reports" and
        // "team/reports/" as different prefixes.
        #expect(nested?.prefix == "team/reports/")

        // However the user typed it.
        #expect(S3APIClient.bucketAndPrefix(from: "my-bucket/")?.bucket == "my-bucket")
        #expect(S3APIClient.bucketAndPrefix(from: "  my-bucket  ")?.bucket == "my-bucket")
    }

    @Test("no path means no bucket scope, so the bucket list is still used")
    func noPathMeansNoScope() {
        #expect(S3APIClient.bucketAndPrefix(from: nil) == nil)
        #expect(S3APIClient.bucketAndPrefix(from: "") == nil)
        #expect(S3APIClient.bucketAndPrefix(from: "/") == nil)
        #expect(S3APIClient.bucketAndPrefix(from: "   ") == nil)
    }

    @Test("credentials carry the scope, and older stored entries decode without one")
    func credentialsRoundTrip() throws {
        let scoped = S3Credentials(
            accessKeyId: "AKIA…",
            secretAccessKey: "secret",
            region: "eu-north-1",
            displayName: "AWS S3",
            rootPath: "/my-bucket/team"
        )
        let data = try JSONEncoder().encode(scoped)
        let decoded = try JSONDecoder().decode(S3Credentials.self, from: data)
        #expect(S3APIClient.bucketAndPrefix(from: decoded.rootPath)?.bucket == "my-bucket")

        // An account stored before bucket scoping existed has no such key;
        // it must keep working and keep its old behaviour.
        let legacy = """
        {"accessKeyId":"AKIA…","secretAccessKey":"secret","region":"eu-north-1","displayName":"AWS S3"}
        """.data(using: .utf8)!
        let legacyDecoded = try JSONDecoder().decode(S3Credentials.self, from: legacy)
        #expect(legacyDecoded.rootPath == nil)
        #expect(S3APIClient.bucketAndPrefix(from: legacyDecoded.rootPath) == nil)
    }
}
