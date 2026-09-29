import Testing
import Foundation
@testable import FileFlussCore

@Suite("Public URL check")
struct PublicURLCheckTests {

    /// A server that never answers says nothing about the link. Treating
    /// that as "not public" is what made a working share link on a
    /// self-hosted server with a self-signed certificate report as private —
    /// and, for direct links, get the account permanently downgraded.
    @Test("A connection that fails leaves the verdict open")
    func unreachableServerIsUnverified() async {
        // Port 1 on loopback: refused immediately, no network involved.
        let url = URL(string: "http://127.0.0.1:1/share/abc")!

        let result = await PublicURLCheck.isReachable(url, timeout: 3)

        #expect(result.outcome == .unverified)
        #expect(result.couldNotCheck)
        #expect(!result.ok)
        #expect(result.detail != nil)
    }

    @Test("An error body is condensed to something readable")
    func summarizesErrorBodies() {
        let html = Data("<html><body><h1>Forbidden</h1><p>Sign in to continue</p></body></html>".utf8)

        let summary = PublicURLCheck.summarize(html)

        #expect(summary.contains("Forbidden"))
        #expect(summary.contains("Sign in to continue"))
        #expect(!summary.contains("<"))
    }

    @Test("An empty body says so rather than returning an empty string")
    func summarizesEmptyBody() {
        #expect(PublicURLCheck.summarize(Data()) == "empty body")
    }
}
