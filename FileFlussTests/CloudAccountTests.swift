import Testing
import Foundation
@testable import FileFluss

@Suite("CloudAccount Tests")
struct CloudAccountTests {

    @Test("All provider types have display names")
    func providerDisplayNames() {
        for type in CloudProviderType.allCases {
            #expect(!type.displayName.isEmpty)
            #expect(!type.icon.isEmpty)
            #expect(!type.id.isEmpty)
        }
    }

    @Test("CloudAccount initializes with defaults")
    func accountDefaults() {
        let account = CloudAccount(providerType: .pCloud)

        #expect(account.displayName == "pCloud")
        #expect(!account.isConnected)
        #expect(account.rootPath == "/")
        #expect(account.lastSyncDate == nil)
    }

    @Test("CloudAccount custom display name overrides default")
    func customDisplayName() {
        let account = CloudAccount(providerType: .oneDrive, displayName: "Work OneDrive")
        #expect(account.displayName == "Work OneDrive")
    }

    @Test("CloudAccount is Codable")
    func codable() throws {
        let original = CloudAccount(
            providerType: .googleDrive,
            displayName: "My Drive",
            isConnected: true,
            rootPath: "/Documents"
        )

        let data = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(CloudAccount.self, from: data)

        #expect(decoded.id == original.id)
        #expect(decoded.providerType == original.providerType)
        #expect(decoded.displayName == original.displayName)
        #expect(decoded.isConnected == original.isConnected)
        #expect(decoded.rootPath == original.rootPath)
    }
}
