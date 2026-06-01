import Foundation

public enum CloudProviderError: LocalizedError {
    case notAuthenticated
    case notImplemented
    case networkError(Error)
    case unauthorized
    /// Server requires a one-time password / TOTP in addition to the
    /// account credentials. Add-account flows catch this to re-prompt the
    /// user without making them retype the password.
    case twoFactorRequired
    case notFound(String)
    case quotaExceeded
    case rateLimited
    case serverError(Int)
    /// Used by non-HTTP transports (SFTP, etc.) to surface a useful
    /// human-readable message instead of a status-code label.
    case commandFailed(String)
    case invalidResponse
    case invalidCredentials
    /// The cloud rejected the upload because the file is too large. `fileBytes`
    /// is the local size; `providerLimitBytes` is the documented limit when
    /// known (nil if we only know the file got rejected).
    case fileTooLarge(fileBytes: Int64, providerLimitBytes: Int64?)

    public var errorDescription: String? {
        switch self {
        case .notAuthenticated: return L10n.text("Not authenticated. Please sign in.")
        case .notImplemented: return L10n.text("This feature is not yet implemented.")
        case .networkError(let error): return L10n.format("Network error: %@", error.localizedDescription)
        case .unauthorized: return L10n.text("Authorization expired. Please sign in again.")
        case .twoFactorRequired: return L10n.text("Two-factor authentication required. Enter your 6-digit code and try again.")
        case .notFound(let path): return L10n.format("Item not found: %@", path)
        case .quotaExceeded: return L10n.text("Storage quota exceeded.")
        case .rateLimited: return L10n.text("Rate limited. Please try again later.")
        case .serverError(let code): return L10n.format("Server error (HTTP %d).", code)
        case .commandFailed(let message): return message
        case .invalidResponse: return L10n.text("Invalid response from server.")
        case .invalidCredentials: return L10n.text("Invalid email or password.")
        case .fileTooLarge(let fileBytes, let limit):
            let size = ByteCountFormatter.string(fromByteCount: fileBytes, countStyle: .file)
            if let limit {
                let limitStr = ByteCountFormatter.string(fromByteCount: limit, countStyle: .file)
                return L10n.format("File is too large to upload (%@). This provider's limit is %@.", size, limitStr)
            }
            return L10n.format("File is too large to upload (%@). The provider rejected it. Try splitting the file or using a different cloud.", size)
        }
    }
}

extension CloudProviderError {
    /// Pre-flight: throw `.fileTooLarge` if `localFile` exceeds the
    /// provider's documented per-file upload limit. Saves a wasted upload
    /// when we already know the server will refuse.
    public static func enforceUploadSizeLimit(_ localFile: URL, provider: CloudProvider) async throws {
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
    public static func sizeLimitError(forStatus status: Int, localFile: URL) -> CloudProviderError? {
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
