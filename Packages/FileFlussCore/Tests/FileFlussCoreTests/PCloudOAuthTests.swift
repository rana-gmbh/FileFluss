import Testing
import Foundation
@testable import FileFlussCore

/// pCloud's browser sign-in (OAuth), and accounts added before it.
@Suite("pCloud sign-in")
struct PCloudOAuthTests {

    /// The code and the app secret go to the host named in the redirect,
    /// so only pCloud's own two are accepted.
    @Test("Only pCloud's own API hosts are accepted")
    func onlyPCloudHosts() throws {
        #expect(try PCloudAPIClient.oauthHost(from: "eapi.pcloud.com") == "eapi.pcloud.com")
        #expect(try PCloudAPIClient.oauthHost(from: "API.pcloud.com") == "api.pcloud.com")
        #expect(throws: CloudProviderError.self) { try PCloudAPIClient.oauthHost(from: "evil.example.com") }
        #expect(throws: CloudProviderError.self) { try PCloudAPIClient.oauthHost(from: "eapi.pcloud.com.evil.example") }
        #expect(throws: CloudProviderError.self) { try PCloudAPIClient.oauthHost(from: nil) }
    }

    @Test("A browser sign-in token is sent as access_token")
    func oauthTokenParameter() {
        let credentials = PCloudCredentials(accessToken: "t", hostname: "eapi.pcloud.com", userId: 1, isOAuth: true)
        #expect(credentials.authQueryItem == URLQueryItem(name: "access_token", value: "t"))
    }

    /// Accounts saved before the browser sign-in have no isOAuth field and
    /// must keep sending their token the way pCloud issued it.
    @Test("An account saved before keeps sending auth")
    func legacyAccount() throws {
        let stored = #"{"accessToken":"t","hostname":"api.pcloud.com","userId":42}"#
        let credentials = try JSONDecoder().decode(PCloudCredentials.self, from: Data(stored.utf8))
        #expect(credentials.authQueryItem == URLQueryItem(name: "auth", value: "t"))
    }

    @Test("Port 80 comes first, so the registered http://localhost works as is")
    func redirectPorts() {
        #expect(PCloudAppCredentials.redirectPorts.first == 80)
    }

    @Test("The token answer is read with either spelling")
    func tokenAnswer() throws {
        let documented = try PCloudAPIClient.parseTokenResponse(Data(#"{"result":0,"access_token":"t","token_type":"bearer","uid":7}"#.utf8))
        #expect(documented.accessToken == "t" && documented.userId == 7)

        let other = try PCloudAPIClient.parseTokenResponse(Data(#"{"result":0,"access_token":"t","userid":"8"}"#.utf8))
        #expect(other.userId == 8)

        let noUser = try PCloudAPIClient.parseTokenResponse(Data(#"{"result":0,"access_token":"t"}"#.utf8))
        #expect(noUser.userId == nil)
    }

    @Test("pCloud's own error text is passed on")
    func tokenError() {
        do {
            _ = try PCloudAPIClient.parseTokenResponse(Data(#"{"result":2012,"error":"Invalid 'code' provided."}"#.utf8))
            Issue.record("expected an error")
        } catch CloudProviderError.commandFailed(let message) {
            #expect(message == "Invalid 'code' provided.")
        } catch {
            Issue.record("unexpected error \(error)")
        }
    }
}
