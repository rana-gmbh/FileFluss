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
        // Use a stub provider that doesn't require authentication
        let provider = GoogleDriveProvider()

        await engine.registerProvider(for: accountId, provider: provider)

        let rule = SyncRule(
            localPath: FileManager.default.temporaryDirectory,
            remotePath: "/test",
            accountId: accountId,
            direction: .upload
        )

        // Stub provider should work without throwing notAuthenticated
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

    @Test("Stub providers return empty list and start unauthenticated")
    func stubProviderInitialState() async {
        let providers: [any CloudProvider] = [
            GoogleDriveProvider(),
            NextCloudProvider(),
            ICloudProvider(),
        ]

        for provider in providers {
            let items = try? await provider.listDirectory(at: "/")
            #expect(items?.isEmpty == true, "\(provider.providerType) should return empty list")
        }
    }

    @Test("OneDriveProvider requires authentication")
    func oneDriveRequiresAuth() async {
        let provider = OneDriveProvider()
        let isAuth = await provider.isAuthenticated
        #expect(isAuth == false)

        await #expect(throws: CloudProviderError.self) {
            _ = try await provider.listDirectory(at: "/")
        }
    }

    @Test("KoofrProvider requires authentication")
    func koofrRequiresAuth() async {
        let provider = KoofrProvider()
        let isAuth = await provider.isAuthenticated
        #expect(isAuth == false)

        await #expect(throws: CloudProviderError.self) {
            _ = try await provider.listDirectory(at: "/")
        }
    }

    @Test("PCloudProvider requires authentication")
    func pcloudRequiresAuth() async {
        let provider = PCloudProvider()
        let isAuth = await provider.isAuthenticated
        #expect(isAuth == false)

        await #expect(throws: CloudProviderError.self) {
            _ = try await provider.listDirectory(at: "/")
        }
    }

    @Test("Provider getFileMetadata throws for stubs")
    func metadataNotImplemented() async {
        let providers: [any CloudProvider] = [
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
