import Foundation

/// Platform shim used by the OAuth-flow providers to launch the system
/// browser at the loopback redirect URL. macOS registers an NSWorkspace
/// implementation at app startup; iOS will register an
/// `ASWebAuthenticationSession`-backed one in Phase 1.
///
/// The providers themselves stay platform-agnostic — they build the auth
/// URL, start an NWListener loopback (pure Foundation Network), and call
/// `BrowserOpener.open(authURL)` to kick the user into the browser.
public enum BrowserOpener {
    nonisolated(unsafe) private static var handler: (@Sendable (URL) -> Void)?

    /// Register the platform-specific URL-open implementation. Call once
    /// at app startup. Subsequent calls overwrite the previous handler.
    public static func register(_ open: @escaping @Sendable (URL) -> Void) {
        handler = open
    }

    /// Open `url` via the registered handler. No-op (and logged) if no
    /// handler was registered — that's a configuration error.
    public static func open(_ url: URL) {
        guard let handler else {
            assertionFailure("BrowserOpener.open called before a handler was registered.")
            return
        }
        handler(url)
    }
}
