import Testing
import Foundation
@testable import FileFluss

@Suite("SyncEngine Tests")
struct SyncEngineTests {

    @Test("Create provider returns correct type for each CloudProviderType")
    func createProviders() async {
        let engine = SyncEngine.shared

        for type in CloudProviderType.allCases {
            let provider = await engine.createProvider(for: type)
            #expect(provider.providerType == type)
        }
    }

    @Test("Register and use provider")
    func registerProvider() async throws {
        let engine = SyncEngine.shared
        let accountId = UUID()
        let provider = PCloudProvider()

        await engine.registerProvider(for: accountId, provider: provider)

        // Verify it doesn't crash when syncing with a registered provider
        let rule = SyncRule(
            localPath: FileManager.default.temporaryDirectory,
            remotePath: "/test",
            accountId: accountId,
            direction: .upload
        )

        // This should work without throwing notAuthenticated
        try await engine.sync(rule: rule)
    }

    @Test("Sync without registered provider throws notAuthenticated")
    func syncWithoutProvider() async {
        let engine = SyncEngine.shared
        let rule = SyncRule(
            localPath: URL(filePath: "/tmp"),
            remotePath: "/test",
            accountId: UUID() // unregistered
        )

        await #expect(throws: CloudProviderError.self) {
            try await engine.sync(rule: rule)
        }
    }

    @Test("Cloud providers conform to protocol and start unauthenticated")
    func providerInitialState() async {
        let providers: [any CloudProvider] = [
            PCloudProvider(),
            OneDriveProvider(),
            GoogleDriveProvider(),
            NextCloudProvider(),
            ICloudProvider(),
        ]

        for provider in providers {
            // All stub providers list empty directories
            let items = try? await provider.listDirectory(at: "/")
            #expect(items?.isEmpty == true, "\(provider.providerType) should return empty list")
        }
    }

    @Test("Provider getFileMetadata throws notImplemented for stubs")
    func metadataNotImplemented() async {
        let providers: [any CloudProvider] = [
            PCloudProvider(),
            OneDriveProvider(),
            GoogleDriveProvider(),
            NextCloudProvider(),
            ICloudProvider(),
        ]

        for provider in providers {
            await #expect(throws: CloudProviderError.self) {
                _ = try await provider.getFileMetadata(at: "/test")
            }
        }
    }
}
