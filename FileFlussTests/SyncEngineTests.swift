import Testing
import Foundation
import FileFlussCore
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

    @Test("Stub providers return empty list and start unauthenticated")
    func stubProviderInitialState() async {
        let providers: [any CloudProvider] = [
            ICloudProvider(),
        ]

        for provider in providers {
            let items = try? await provider.listDirectory(at: "/")
            #expect(items?.isEmpty == true, "\(provider.providerType) should return empty list")
        }
    }

    @Test("GoogleDriveProvider requires authentication")
    func googleDriveRequiresAuth() async {
        let provider = GoogleDriveProvider()
        let isAuth = await provider.isAuthenticated
        #expect(isAuth == false)

        await #expect(throws: CloudProviderError.self) {
            _ = try await provider.listDirectory(at: "/")
        }
    }

    @Test("NextCloudProvider requires authentication")
    func nextCloudRequiresAuth() async {
        let provider = NextCloudProvider()
        let isAuth = await provider.isAuthenticated
        #expect(isAuth == false)

        await #expect(throws: CloudProviderError.self) {
            _ = try await provider.listDirectory(at: "/")
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

    @Test("DropboxProvider requires authentication")
    func dropboxRequiresAuth() async {
        let provider = DropboxProvider()
        let isAuth = await provider.isAuthenticated
        #expect(isAuth == false)

        await #expect(throws: CloudProviderError.self) {
            _ = try await provider.listDirectory(at: "/")
        }
    }

    @Test("Provider getFileMetadata throws for stubs")
    func metadataNotImplemented() async {
        let providers: [any CloudProvider] = [
            ICloudProvider(),
        ]

        for provider in providers {
            await #expect(throws: CloudProviderError.self) {
                _ = try await provider.getFileMetadata(at: "/test")
            }
        }
    }
}
