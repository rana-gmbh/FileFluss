import Testing
import Foundation
@testable import FileFlussCore

/// Jottacloud rotates the refresh token on every refresh and revokes the
/// whole token family when an old one is presented again, which signs the
/// user out. Concurrent callers after expiry must share a single refresh.
@Suite("Jottacloud token refresh")
struct JottacloudTokenRefreshTests {

    /// Records what the token endpoint was sent.
    private actor TokenEndpoint {
        private(set) var sentRefreshTokens: [String] = []
        private var generation = 0
        var failNext = false

        func refresh(_ form: [String: String]) async throws -> JottacloudAPIClient.TokenResponse {
            sentRefreshTokens.append(form["refresh_token"] ?? "")
            // Long enough for every concurrent caller to arrive meanwhile.
            try await Task.sleep(for: .milliseconds(200))
            if failNext {
                failNext = false
                throw CloudProviderError.serverError(400)
            }
            generation += 1
            return .init(accessToken: "access-\(generation)", refreshToken: "refresh-\(generation)", expiresIn: 3600)
        }

        func setFailNext() { failNext = true }
    }

    private final class Persisted: @unchecked Sendable {
        private let lock = NSLock()
        private var values: [String] = []
        func append(_ value: String) { lock.lock(); values.append(value); lock.unlock() }
        var all: [String] { lock.lock(); defer { lock.unlock() }; return values }
    }

    private func makeClient(_ endpoint: TokenEndpoint, persisted: Persisted) -> JottacloudAPIClient {
        JottacloudAPIClient(
            credentials: JottacloudCredentials(username: "u", refreshToken: "refresh-0", tokenEndpoint: "https://id.example/token"),
            onCredentialsChanged: { persisted.append($0.refreshToken) },
            tokenPoster: { _, form in try await endpoint.refresh(form) }
        )
    }

    @Test("Concurrent callers after expiry spend the refresh token once")
    func singleFlight() async throws {
        let endpoint = TokenEndpoint()
        let persisted = Persisted()
        let client = makeClient(endpoint, persisted: persisted)

        let tokens = try await withThrowingTaskGroup(of: String.self) { group in
            for _ in 0..<8 { group.addTask { try await client.validAccessToken() } }
            return try await group.reduce(into: [String]()) { $0.append($1) }
        }

        #expect(tokens == Array(repeating: "access-1", count: 8))
        #expect(await endpoint.sentRefreshTokens == ["refresh-0"])
        // The rotated token is persisted, once.
        #expect(persisted.all == ["refresh-1"])
        #expect(await client.credentials.refreshToken == "refresh-1")

        // A fresh token is reused without another refresh.
        #expect(try await client.validAccessToken() == "access-1")
        #expect(await endpoint.sentRefreshTokens == ["refresh-0"])
    }

    @Test("A failed refresh fails every waiting caller, and the next call tries again")
    func failure() async throws {
        let endpoint = TokenEndpoint()
        await endpoint.setFailNext()
        let client = makeClient(endpoint, persisted: Persisted())

        let failures = await withTaskGroup(of: Bool.self) { group in
            for _ in 0..<4 {
                group.addTask {
                    do { _ = try await client.validAccessToken(); return false } catch { return true }
                }
            }
            return await group.reduce(into: 0) { $0 += $1 ? 1 : 0 }
        }
        #expect(failures == 4)
        #expect(await endpoint.sentRefreshTokens == ["refresh-0"])

        #expect(try await client.validAccessToken() == "access-1")
        #expect(await endpoint.sentRefreshTokens == ["refresh-0", "refresh-0"])
    }
}
