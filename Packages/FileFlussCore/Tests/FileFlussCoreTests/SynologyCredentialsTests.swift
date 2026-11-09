import Testing
import Foundation
@testable import FileFlussCore

/// The device token that lets a Synology account with DSM two-factor
/// authentication log in again without a new code (#66, #67).
@Suite("Synology credentials")
struct SynologyCredentialsTests {

    private func credentials(deviceId: String? = nil) -> SynologyDriveCredentials {
        SynologyDriveCredentials(
            serverURL: "https://nas.example.com:5001",
            username: "robert",
            password: "secret",
            allowSelfSignedCertificate: false,
            displayName: "robert@nas.example.com",
            deviceId: deviceId
        )
    }

    /// Accounts saved before the token existed have no such field. They must
    /// still decode, or an update would lock people out of their accounts.
    @Test("Credentials saved before device tokens existed still decode")
    func decodesWithoutDeviceId() throws {
        let legacy = """
        {
          "serverURL": "https://nas.example.com:5001",
          "username": "robert",
          "password": "secret",
          "allowSelfSignedCertificate": false,
          "displayName": "robert@nas.example.com"
        }
        """

        let decoded = try JSONDecoder().decode(
            SynologyDriveCredentials.self, from: Data(legacy.utf8)
        )

        #expect(decoded.username == "robert")
        #expect(decoded.deviceId == nil)
    }

    @Test("A device token survives being stored and read back")
    func roundTripsDeviceId() throws {
        let data = try JSONEncoder().encode(credentials(deviceId: "token-abc"))
        let decoded = try JSONDecoder().decode(SynologyDriveCredentials.self, from: data)

        #expect(decoded.deviceId == "token-abc")
    }

    /// A token belongs to one DSM account. Comparing a stored server URL
    /// with a freshly typed one has to survive the ways people type the
    /// same address, or editing an account quietly asks for an OTP again.
    @Test("The same server typed differently is the same server")
    func normalisesServerURLs() {
        let canonical = SynologyDriveAPIClient.normalizeServerURL("https://nas.example.com:5001")

        #expect(SynologyDriveAPIClient.normalizeServerURL("https://nas.example.com:5001/") == canonical)
        #expect(SynologyDriveAPIClient.normalizeServerURL(" https://nas.example.com:5001 ") == canonical)
    }
}
