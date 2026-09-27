import Testing
import Foundation
import FileFlussCore

/// Pins FileFluss's SigV4 query-string signing against the example AWS
/// publishes for presigned GET URLs ("Signature Calculations for the
/// Authorization Header: Transferring Payload… — Query Parameters").
/// A share link that is signed wrong fails only when the *recipient*
/// opens it, so this has to be caught here rather than in the field.
@Suite("S3 presigned URLs")
struct S3PresignTests {

    /// AWS's documented example credentials and request.
    private let accessKey = "AKIAIOSFODNN7EXAMPLE"
    private let secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"

    private var referenceDate: Date {
        var components = DateComponents()
        components.year = 2013
        components.month = 5
        components.day = 24
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        return calendar.date(from: components)!
    }

    @Test("matches AWS's published signature for the documented example")
    func matchesAWSExampleVector() throws {
        let url = try S3APIClient.presignedGetURL(
            rawHost: "examplebucket.s3.amazonaws.com",
            key: "test.txt",
            region: "us-east-1",
            accessKeyId: accessKey,
            secretAccessKey: secret,
            expiresInSeconds: 86400,
            attachmentFilename: nil,
            now: referenceDate
        )

        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }

        #expect(url.host == "examplebucket.s3.amazonaws.com")
        #expect(url.path == "/test.txt")
        #expect(value("X-Amz-Algorithm") == "AWS4-HMAC-SHA256")
        #expect(value("X-Amz-Credential") == "AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request")
        #expect(value("X-Amz-Date") == "20130524T000000Z")
        #expect(value("X-Amz-Expires") == "86400")
        #expect(value("X-Amz-SignedHeaders") == "host")
        #expect(value("X-Amz-Signature") == "aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404")
    }

    @Test("signs the lowercased host, so a mixed-case bucket still verifies")
    func lowercasesHostBeforeSigning() throws {
        // Backblaze allows mixed-case bucket names; browsers lowercase the
        // host before sending it. If the signature covered the mixed-case
        // spelling, the recipient would get SignatureDoesNotMatch.
        func signature(host: String) throws -> String? {
            let url = try S3APIClient.presignedGetURL(
                rawHost: host,
                key: "test.txt",
                region: "us-east-1",
                accessKeyId: accessKey,
                secretAccessKey: secret,
                expiresInSeconds: 86400,
                attachmentFilename: nil,
                now: referenceDate
            )
            let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            return items.first { $0.name == "X-Amz-Signature" }?.value
        }

        let mixed = try signature(host: "FileFluss.s3.eu-central-003.backblazeb2.com")
        let lower = try signature(host: "filefluss.s3.eu-central-003.backblazeb2.com")
        #expect(mixed == lower)
        #expect(mixed?.isEmpty == false)
    }

    @Test("signs the content-disposition parameter it adds")
    func includesAttachmentFilenameInSignature() throws {
        func signature(filename: String?) throws -> String? {
            let url = try S3APIClient.presignedGetURL(
                rawHost: "examplebucket.s3.amazonaws.com",
                key: "holiday photo.jpg",
                region: "eu-central-1",
                accessKeyId: accessKey,
                secretAccessKey: secret,
                expiresInSeconds: 3600,
                attachmentFilename: filename,
                now: referenceDate
            )
            let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            return items.first { $0.name == "X-Amz-Signature" }?.value
        }

        // The disposition parameter is part of the canonical request, so
        // adding it must change the signature — if it didn't, S3 would
        // reject the link as tampered with.
        let withName = try signature(filename: "holiday photo.jpg")
        let without = try signature(filename: nil)
        #expect(withName != without)
        #expect(withName?.isEmpty == false)
    }
}
