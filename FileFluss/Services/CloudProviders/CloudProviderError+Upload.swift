import Foundation
import FileFlussCore

/// macOS-side extension to `CloudProviderError` that depends on the
/// `CloudProvider` protocol — which still lives in the macOS app until
/// Phase 0 step 4 moves it (along with `CloudFileItem` and
/// `ByteProgressHandler`) into FileFlussCore. Once the protocol is in the
/// package this extension folds back into Packages/FileFlussCore/Sources/
/// FileFlussCore/CloudProviderError.swift and this file goes away.
extension CloudProviderError {
    /// Pre-flight: throw `.fileTooLarge` if `localFile` exceeds the
    /// provider's documented per-file upload limit. Saves a wasted upload
    /// when we already know the server will refuse.
    static func enforceUploadSizeLimit(_ localFile: URL, provider: CloudProvider) async throws {
        guard let limit = await provider.maxUploadFileSize else { return }
        let attrs = try? FileManager.default.attributesOfItem(atPath: localFile.path)
        let fileBytes = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
        if fileBytes > limit {
            throw CloudProviderError.fileTooLarge(fileBytes: fileBytes, providerLimitBytes: limit)
        }
    }
}
