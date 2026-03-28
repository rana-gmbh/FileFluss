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
        }
    }
}
