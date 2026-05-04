import Foundation

enum CloudProviderError: LocalizedError {
    case notAuthenticated
    case notImplemented
    case networkError(Error)
    case unauthorized
    case notFound(String)
    case quotaExceeded
    case rateLimited
    case serverError(Int)
    case invalidResponse
    case invalidCredentials
    /// The cloud rejected the upload because the file is too large. `fileBytes`
    /// is the local size; `providerLimitBytes` is the documented limit when
    /// known (nil if we only know the file got rejected).
    case fileTooLarge(fileBytes: Int64, providerLimitBytes: Int64?)

    var errorDescription: String? {
        switch self {
        case .notAuthenticated: return "Not authenticated. Please sign in."
        case .notImplemented: return "This feature is not yet implemented."
        case .networkError(let error): return "Network error: \(error.localizedDescription)"
        case .unauthorized: return "Authorization expired. Please sign in again."
        case .notFound(let path): return "Item not found: \(path)"
        case .quotaExceeded: return "Storage quota exceeded."
        case .rateLimited: return "Rate limited. Please try again later."
        case .serverError(let code): return "Server error (HTTP \(code))."
        case .invalidResponse: return "Invalid response from server."
        case .invalidCredentials: return "Invalid email or password."
        case .fileTooLarge(let fileBytes, let limit):
            let size = ByteCountFormatter.string(fromByteCount: fileBytes, countStyle: .file)
            if let limit {
                let limitStr = ByteCountFormatter.string(fromByteCount: limit, countStyle: .file)
                return "File is too large to upload (\(size)). This provider's limit is \(limitStr)."
            }
            return "File is too large to upload (\(size)). The provider rejected it. Try splitting the file or using a different cloud."
        }
    }
}

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

    /// Helper used by upload paths: if the server returned a status that
    /// commonly indicates a size-limit rejection (413 always, 422 only when
    /// the file is suspiciously large), translate it to `.fileTooLarge`.
    /// Returns `nil` if the response is unrelated to file size.
    static func sizeLimitError(forStatus status: Int, localFile: URL) -> CloudProviderError? {
        let attrs = try? FileManager.default.attributesOfItem(atPath: localFile.path)
        let fileBytes = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
        switch status {
        case 413:
            // 413 Payload Too Large is unambiguous.
            return .fileTooLarge(fileBytes: fileBytes, providerLimitBytes: nil)
        case 422:
            // 422 Unprocessable Entity is reused by WebDAV servers for size
            // rejection but also for other things. Only translate when the
            // file is large enough that a size limit is the likely cause.
            if fileBytes > 2_000_000_000 {
                return .fileTooLarge(fileBytes: fileBytes, providerLimitBytes: nil)
            }
            return nil
        default:
            return nil
        }
    }
}
