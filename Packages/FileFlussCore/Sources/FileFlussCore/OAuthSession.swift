import Foundation

/// What the OAuth server's redirect carries back, plus the redirect URI
/// the provider should plug into its token-exchange POST. The same string
/// has to appear in both the authorize request and the token-exchange
/// request — OAuth servers cross-check them.
public struct OAuthCallback: Sendable {
    /// e.g. `http://localhost:54321` on macOS (loopback), `filefluss-oauth://callback`
    /// on iOS (ASWebAuthenticationSession with a custom URL scheme).
    public let redirectURI: String

    /// Full callback URL the browser was redirected to, including
    /// `?code=…&state=…` (or `?error=…` if the user cancelled).
    public let callbackURL: URL

    public init(redirectURI: String, callbackURL: URL) {
        self.redirectURI = redirectURI
        self.callbackURL = callbackURL
    }
}

/// Platform-specific machinery that runs an OAuth 2.0 authorization-code
/// flow end-to-end. The provider builds its own auth URL once given a
/// redirect URI, and the authenticator handles "open browser, wait for
/// the callback, hand the response back."
///
/// - macOS: an NWListener-backed loopback authenticator opens the system
///   browser via `NSWorkspace.shared.open` and parses the inbound HTTP
///   request.
/// - iOS: an `ASWebAuthenticationSession`-backed authenticator opens an
///   in-app web view and routes the redirect through a custom URL scheme
///   registered in the app's Info.plist.
public protocol OAuthAuthenticator: Sendable {
    /// - Parameters:
    ///   - callbackURLScheme: scheme that the OAuth server should redirect
    ///     to. Used by the iOS authenticator to match the redirect; the
    ///     macOS authenticator ignores it (loopback gets a localhost URL).
    ///   - authURLBuilder: invoked once the authenticator knows what the
    ///     redirect URI will be (a freshly-allocated loopback port on
    ///     macOS, or `<scheme>://callback` on iOS). The provider returns
    ///     its fully-formed authorize URL with that redirect baked in.
    /// - Returns: The callback URL the browser redirected to, together
    ///   with the exact redirect-URI string used — the provider must pass
    ///   that same string back to the token-exchange POST so the server's
    ///   redirect_uri cross-check passes.
    func authenticate(
        callbackURLScheme: String,
        authURLBuilder: @escaping @Sendable (_ redirectURI: String) -> URL
    ) async throws -> OAuthCallback
}

/// Process-wide registry. The host app (macOS or iOS) registers a
/// platform-specific `OAuthAuthenticator` at launch; the OAuth-flow
/// providers call `authenticate` without caring which platform they're on.
public enum OAuthSession {
    nonisolated(unsafe) private static var authenticator: OAuthAuthenticator?

    public static func register(_ authenticator: any OAuthAuthenticator) {
        Self.authenticator = authenticator
    }

    public static func authenticate(
        callbackURLScheme: String,
        authURLBuilder: @escaping @Sendable (_ redirectURI: String) -> URL
    ) async throws -> OAuthCallback {
        guard let authenticator else {
            assertionFailure("OAuthSession.authenticate called before an authenticator was registered.")
            throw CloudProviderError.notImplemented
        }
        return try await authenticator.authenticate(
            callbackURLScheme: callbackURLScheme,
            authURLBuilder: authURLBuilder
        )
    }
}
