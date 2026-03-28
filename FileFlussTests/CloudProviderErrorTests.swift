import Testing
import Foundation
@testable import FileFluss

@Suite("CloudProviderError Tests")
struct CloudProviderErrorTests {

    @Test("All error cases have descriptions")
    func errorDescriptions() {
        let cases: [CloudProviderError] = [
            .notAuthenticated,
            .notImplemented,
            .networkError(URLError(.notConnectedToInternet)),
            .unauthorized,
            .notFound("/test/path"),
            .quotaExceeded,
            .rateLimited,
            .serverError(500),
            .invalidResponse,
        ]

        for error in cases {
            #expect(error.errorDescription != nil, "Missing description for \(error)")
            #expect(!error.errorDescription!.isEmpty)
        }
    }

    @Test("Not found error includes path")
    func notFoundIncludesPath() {
        let error = CloudProviderError.notFound("/my/file.txt")
        #expect(error.errorDescription!.contains("/my/file.txt"))
    }

    @Test("Server error includes HTTP code")
    func serverErrorIncludesCode() {
        let error = CloudProviderError.serverError(503)
        #expect(error.errorDescription!.contains("503"))
    }
}
