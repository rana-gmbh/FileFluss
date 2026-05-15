import AppKit
import Foundation
import Network
import os
import FileFlussCore

private let oauthLog = Logger(subsystem: "com.rana.FileFluss", category: "loopbackOAuth")

/// macOS implementation of `OAuthAuthenticator`. Each call:
/// 1. Starts an `NWListener` on a random TCP port (`http://localhost:<port>`).
/// 2. Asks the provider to build the authorize URL with that loopback as
///    the redirect URI.
/// 3. Opens the system browser at that URL via `NSWorkspace`.
/// 4. Waits up to 5 minutes for the browser to redirect back to the
///    loopback listener with `?code=…&state=…`.
/// 5. Returns the callback URL plus the redirect URI string (the provider
///    has to pass the same string to its token-exchange POST).
///
/// Generalised out of each OAuth provider's inline NWListener block — Box,
/// Dropbox, Google Drive, OneDrive, and NextCloud all had near-identical
/// copies of this code. Centralising here lets the package stay platform-
/// agnostic and lets iOS plug in `ASWebAuthenticationSession` for the
/// same call site.
struct LoopbackOAuthAuthenticator: OAuthAuthenticator {
    /// How long to wait for the user to complete the browser handshake.
    private static let timeout: TimeInterval = 300

    func authenticate(
        callbackURLScheme _: String,
        authURLBuilder: @escaping @Sendable (_ redirectURI: String) -> URL
    ) async throws -> OAuthCallback {
        let listener = try NWListener(using: .tcp, on: .any)
        let guard_ = ContinuationGuard<OAuthCallback>()

        return try await withCheckedThrowingContinuation { continuation in
            guard_.setContinuation(continuation)

            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let port = listener.port?.rawValue else { return }
                    let redirectURI = "http://localhost:\(port)"
                    let authURL = authURLBuilder(redirectURI)
                    DispatchQueue.main.async {
                        NSWorkspace.shared.open(authURL)
                    }
                    // Stash the redirect URI on the guard so it survives
                    // until the new-connection handler resumes.
                    guard_.setRedirectURI(redirectURI)
                case .failed(let error):
                    guard_.resume(throwing: CloudProviderError.networkError(error))
                default:
                    break
                }
            }

            listener.newConnectionHandler = { connection in
                connection.start(queue: .global())
                connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, _, _ in
                    guard let data, let requestString = String(data: data, encoding: .utf8) else {
                        connection.cancel()
                        return
                    }

                    guard let firstLine = requestString.components(separatedBy: "\r\n").first,
                          let urlPart = firstLine.split(separator: " ").dropFirst().first else {
                        connection.cancel()
                        return
                    }

                    let callbackURLString = "http://localhost\(urlPart)"
                    let components = URLComponents(string: callbackURLString)

                    if let errorParam = components?.queryItems?.first(where: { $0.name == "error" })?.value {
                        oauthLog.error("[OAuth] error: \(errorParam)")
                        let errorHTML = "<!DOCTYPE html><html><body><h2>Authentication failed</h2><p>\(errorParam)</p><p>You can close this window.</p></body></html>"
                        let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(errorHTML.utf8.count)\r\nConnection: close\r\n\r\n\(errorHTML)"
                        connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        listener.cancel()
                        guard_.resume(throwing: CloudProviderError.unauthorized)
                        return
                    }

                    guard components?.queryItems?.contains(where: { $0.name == "code" }) == true else {
                        // Favicon / preflight / DNS probe — keep listening.
                        let emptyResponse = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        connection.send(content: emptyResponse.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        return
                    }

                    let successHTML = "<!DOCTYPE html><html><body><h2>Signed in to FileFluss</h2><p>You can close this window and return to FileFluss.</p></body></html>"
                    let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(successHTML.utf8.count)\r\nConnection: close\r\n\r\n\(successHTML)"
                    connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                        connection.cancel()
                    })

                    listener.cancel()

                    guard let callbackURL = URL(string: callbackURLString),
                          let redirectURI = guard_.redirectURI else {
                        guard_.resume(throwing: CloudProviderError.invalidResponse)
                        return
                    }
                    guard_.resume(returning: OAuthCallback(redirectURI: redirectURI, callbackURL: callbackURL))
                }
            }

            listener.start(queue: .global())

            DispatchQueue.global().asyncAfter(deadline: .now() + Self.timeout) {
                listener.cancel()
                guard_.resume(throwing: CloudProviderError.notAuthenticated)
            }
        }
    }
}

/// Single-shot guard around a `CheckedContinuation` so the multiple
/// callback sites in NWListener can race without resuming twice. Also
/// stashes the redirect URI that the state-ready handler computes, since
/// the new-connection handler needs it to build the OAuthCallback.
private final class ContinuationGuard<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<T, Error>?
    private(set) var redirectURI: String?

    func setContinuation(_ continuation: CheckedContinuation<T, Error>) {
        lock.lock(); defer { lock.unlock() }
        self.continuation = continuation
    }

    func setRedirectURI(_ uri: String) {
        lock.lock(); defer { lock.unlock() }
        redirectURI = uri
    }

    func resume(returning value: T) {
        lock.lock(); defer { lock.unlock() }
        continuation?.resume(returning: value)
        continuation = nil
    }

    func resume(throwing error: Error) {
        lock.lock(); defer { lock.unlock() }
        continuation?.resume(throwing: error)
        continuation = nil
    }
}
