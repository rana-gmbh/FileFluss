import AppKit
import WebKit
import os

private let pickerLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDrivePicker")

/// Presents the Google Drive Picker in a native macOS window hosting a WKWebView.
/// The web page bootstraps Google's Picker JS SDK, and selected folders are
/// returned to Swift via a `WKScriptMessageHandler` bridge.
///
/// Under the `drive.file` OAuth scope, this is the only way to give FileFluss
/// access to folders in the user's Drive — the app cannot enumerate Drive
/// content on its own.
@MainActor
final class GoogleDrivePickerWindow: NSObject {
    private var window: NSWindow?
    private var webView: WKWebView?
    private var continuation: CheckedContinuation<[PickedDriveFolder], Error>?
    private var isReady = false
    private var pendingInitScript: String?

    private let accessToken: String
    private let apiKey: String
    private let preselectFileIds: [String]

    init(accessToken: String, apiKey: String, preselectFileIds: [String] = []) {
        self.accessToken = accessToken
        self.apiKey = apiKey
        self.preselectFileIds = preselectFileIds
    }

    /// Presents the picker window and suspends until the user picks folders,
    /// cancels, or closes the window. Returns an empty array on cancel.
    func present() async throws -> [PickedDriveFolder] {
        if apiKey.isEmpty {
            throw GoogleDrivePickerError.missingApiKey
        }

        return try await withCheckedThrowingContinuation { cont in
            self.continuation = cont
            self.showWindow()
        }
    }

    private func showWindow() {
        let config = WKWebViewConfiguration()
        let bridge = PickerBridge(owner: self)
        config.userContentController.add(bridge, name: "picker")
        config.preferences.javaScriptCanOpenWindowsAutomatically = true

        let webView = WKWebView(
            frame: NSRect(x: 0, y: 0, width: 960, height: 680),
            configuration: config
        )
        webView.navigationDelegate = self
        self.webView = webView

        guard let htmlURL = Bundle.main.url(
            forResource: "picker",
            withExtension: "html",
            subdirectory: "GoogleDrivePicker"
        ) ?? Bundle.main.url(forResource: "picker", withExtension: "html") else {
            finishWithError(GoogleDrivePickerError.bundleResourceMissing)
            return
        }

        // allowingReadAccessTo the parent dir so Picker JS's iframe can resolve.
        webView.loadFileURL(htmlURL, allowingReadAccessTo: htmlURL.deletingLastPathComponent())

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 960, height: 680),
            styleMask: [.titled, .closable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Choose Google Drive folders"
        window.contentView = webView
        window.center()
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.makeKeyAndOrderFront(nil)
        self.window = window
    }

    fileprivate func handleBridgeMessage(_ body: Any) {
        guard let dict = body as? [String: Any],
              let action = dict["action"] as? String else {
            pickerLog.error("Picker bridge received malformed message: \(String(describing: body))")
            return
        }

        switch action {
        case "ready":
            isReady = true
            runInitScript()

        case "picked":
            let rawFolders = (dict["folders"] as? [[String: Any]]) ?? []
            let folders: [PickedDriveFolder] = rawFolders.compactMap { d in
                guard let id = d["id"] as? String,
                      let name = d["name"] as? String,
                      (d["mimeType"] as? String) == "application/vnd.google-apps.folder" else {
                    return nil
                }
                return PickedDriveFolder(id: id, name: name)
            }
            pickerLog.info("Picker returned \(folders.count) folder(s)")
            finishWithResult(folders)

        case "cancel":
            pickerLog.info("Picker cancelled by user")
            finishWithResult([])

        case "error":
            let msg = (dict["message"] as? String) ?? "Unknown picker error"
            pickerLog.error("Picker JS error: \(msg)")
            finishWithError(GoogleDrivePickerError.jsError(msg))

        default:
            break
        }
    }

    private func runInitScript() {
        let payload: [String: Any] = [
            "accessToken": accessToken,
            "apiKey": apiKey,
            "preselectFileIds": preselectFileIds,
        ]
        guard let json = try? JSONSerialization.data(withJSONObject: payload),
              let jsonString = String(data: json, encoding: .utf8) else {
            finishWithError(GoogleDrivePickerError.serializationFailed)
            return
        }
        let js = "window.__fileflussInit(\(jsonString));"
        webView?.evaluateJavaScript(js) { [weak self] _, error in
            if let error {
                self?.finishWithError(error)
            }
        }
    }

    private func finishWithResult(_ folders: [PickedDriveFolder]) {
        guard let cont = continuation else { return }
        continuation = nil
        cont.resume(returning: folders)
        closeWindow()
    }

    private func finishWithError(_ error: Error) {
        guard let cont = continuation else { return }
        continuation = nil
        cont.resume(throwing: error)
        closeWindow()
    }

    private func closeWindow() {
        window?.delegate = nil
        window?.close()
        window = nil
        webView = nil
    }
}

// MARK: - WKNavigationDelegate

extension GoogleDrivePickerWindow: WKNavigationDelegate {
    nonisolated func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            if self.isReady {
                self.runInitScript()
            }
            // If the JS hasn't posted "ready" yet, runInitScript will fire from
            // handleBridgeMessage once it does.
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        Task { @MainActor [weak self] in
            self?.finishWithError(error)
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        Task { @MainActor [weak self] in
            self?.finishWithError(error)
        }
    }
}

// MARK: - NSWindowDelegate

extension GoogleDrivePickerWindow: NSWindowDelegate {
    nonisolated func windowWillClose(_ notification: Notification) {
        Task { @MainActor [weak self] in
            // User closed the window without picking — treat as cancel.
            self?.finishWithResult([])
        }
    }
}

// MARK: - Bridge (separate object to avoid retain cycles via WKUserContentController)

private final class PickerBridge: NSObject, WKScriptMessageHandler {
    private weak var owner: GoogleDrivePickerWindow?

    init(owner: GoogleDrivePickerWindow) {
        self.owner = owner
    }

    nonisolated func userContentController(
        _ userContentController: WKUserContentController,
        didReceive message: WKScriptMessage
    ) {
        let body = message.body
        Task { @MainActor [weak owner] in
            owner?.handleBridgeMessage(body)
        }
    }
}

// MARK: - Errors

enum GoogleDrivePickerError: LocalizedError {
    case missingApiKey
    case bundleResourceMissing
    case serializationFailed
    case jsError(String)

    var errorDescription: String? {
        switch self {
        case .missingApiKey:
            return "Google Picker API key is not configured. Add GOOGLE_PICKER_API_KEY to Info.plist."
        case .bundleResourceMissing:
            return "picker.html is missing from the app bundle."
        case .serializationFailed:
            return "Failed to serialize picker init payload."
        case .jsError(let msg):
            return "Google Picker error: \(msg)"
        }
    }
}
