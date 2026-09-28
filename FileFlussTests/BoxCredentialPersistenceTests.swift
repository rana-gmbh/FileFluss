import Testing
import Foundation
import FileFlussCore

/// Box issues a NEW refresh token on every refresh and invalidates the
/// previous one. A rotated token that only ever lives in memory means the
/// stored one is already dead, and the next launch demands a fresh sign-in
/// — which is exactly what users reported in issue #46.
@Suite("Box credential persistence")
struct BoxCredentialPersistenceTests {

    @Test("the client hands out rotated credentials so they can be stored")
    func rotationIsObservable() async {
        let original = BoxCredentials(
            accessToken: "access-1",
            refreshToken: "refresh-1",
            expiresAt: Date().addingTimeInterval(3600),
            userLogin: "someone@example.com",
            displayName: "Someone"
        )
        let client = BoxAPIClient(credentials: original)

        // The callback is what carries a rotated token to the keychain. If
        // this ever stops being wired, the bug returns silently — nothing
        // fails at the time, only on the next launch.
        let box = RotationBox()
        await client.setCredentialsDidChange { creds in
            box.record(creds)
        }
        #expect(box.recorded == nil, "nothing should be recorded before a refresh")
    }

    @Test("credentials survive a round trip through storage")
    func credentialsRoundTrip() throws {
        let creds = BoxCredentials(
            accessToken: "access-1",
            refreshToken: "refresh-2",
            expiresAt: Date(timeIntervalSince1970: 1_800_000_000),
            userLogin: "someone@example.com",
            displayName: "Someone"
        )
        let data = try JSONEncoder().encode(creds)
        let decoded = try JSONDecoder().decode(BoxCredentials.self, from: data)
        #expect(decoded.refreshToken == "refresh-2")
        #expect(decoded.userLogin == "someone@example.com")
    }
}

/// Small thread-safe sink; the callback is `@Sendable`.
private final class RotationBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: BoxCredentials?

    var recorded: BoxCredentials? {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    func record(_ creds: BoxCredentials) {
        lock.lock(); defer { lock.unlock() }
        value = creds
    }
}
