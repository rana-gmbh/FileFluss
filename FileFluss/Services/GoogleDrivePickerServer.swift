import AppKit
import Foundation
import Network
import os
import FileFlussCore

private let pickerServerLog = Logger(subsystem: "com.rana.FileFluss", category: "gdrivePickerServer")

/// Hosts the Google Picker in the user's **real browser** rather than an
/// embedded `WKWebView`. The Picker needs the user's signed-in Google session
/// (cookies); a `WKWebView` has none and macOS blocks the Picker iframe's
/// third-party cookies, which produced "Can't access your Google Account".
/// The system browser already has that session, so the Picker works there.
///
/// Flow: start a localhost `NWListener`, open `http://localhost:<port>/` in the
/// browser (serving a page that runs the Picker with the supplied OAuth token),
/// and wait for the page to report the chosen folders back via
/// `GET /result?data=…`. Mirrors `LoopbackOAuthAuthenticator`.
struct GoogleDrivePickerServer {
    private static let timeout: TimeInterval = 300

    /// Opens the Picker in the browser and returns the folders the user chose.
    /// An empty array means the user cancelled.
    func pickFolders(accessToken: String) async throws -> [GoogleDrivePickedRoot] {
        let listener = try NWListener(using: .tcp, on: .any)
        let guard_ = PickerContinuationGuard()
        let apiKey = GoogleDrivePickerAPIClient.pickerAPIKey

        return try await withCheckedThrowingContinuation { continuation in
            guard_.setContinuation(continuation)

            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let port = listener.port?.rawValue else { return }
                    let url = URL(string: "http://localhost:\(port)/")!
                    DispatchQueue.main.async { NSWorkspace.shared.open(url) }
                case .failed(let error):
                    guard_.resume(throwing: CloudProviderError.networkError(error))
                default:
                    break
                }
            }

            listener.newConnectionHandler = { connection in
                connection.start(queue: .global())
                connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { data, _, _, _ in
                    guard let data, let request = String(data: data, encoding: .utf8),
                          let firstLine = request.components(separatedBy: "\r\n").first,
                          let pathPart = firstLine.split(separator: " ").dropFirst().first else {
                        connection.cancel(); return
                    }
                    let path = String(pathPart)

                    if path.hasPrefix("/result") {
                        let components = URLComponents(string: "http://localhost\(path)")
                        let action = components?.queryItems?.first(where: { $0.name == "action" })?.value
                        var roots: [GoogleDrivePickedRoot] = []
                        if action == "picked",
                           let dataParam = components?.queryItems?.first(where: { $0.name == "data" })?.value,
                           let jsonData = dataParam.data(using: .utf8),
                           let docs = try? JSONDecoder().decode([PickedDoc].self, from: jsonData) {
                            roots = docs.map { GoogleDrivePickedRoot(id: $0.id, name: $0.name) }
                        }
                        Self.respond(connection, html: Self.donePage)
                        listener.cancel()
                        guard_.resume(returning: roots)
                        return
                    }

                    if path == "/" || path.hasPrefix("/?") || path == "/index.html" {
                        Self.respond(connection, html: Self.pickerPage(accessToken: accessToken, apiKey: apiKey))
                        return  // keep listening for /result
                    }

                    // favicon and friends
                    Self.respond(connection, status: "404 Not Found", html: "")
                }
            }

            listener.start(queue: .global())

            DispatchQueue.global().asyncAfter(deadline: .now() + Self.timeout) {
                listener.cancel()
                guard_.resume(returning: [])   // treat a timeout as a cancel
            }
        }
    }

    private static func respond(_ connection: NWConnection, status: String = "200 OK", html: String) {
        let response = "HTTP/1.1 \(status)\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(html.utf8.count)\r\nConnection: close\r\n\r\n\(html)"
        connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
            connection.cancel()
        })
    }

    private struct PickedDoc: Decodable { let id: String; let name: String }

    private static let donePage = """
    <!DOCTYPE html><html><head><meta charset="utf-8"></head>
    <body style="font-family:-apple-system,sans-serif;text-align:center;padding-top:80px;color:#333;">
    <h2>All set</h2><p>You can close this tab and return to FileFluss.</p></body></html>
    """

    private static func pickerPage(accessToken: String, apiKey: String) -> String {
        let tokenJSON = "\"\(accessToken)\""
        let keyJSON = "\"\(apiKey)\""
        let appIdJSON = "\"\(GoogleDrivePickerAPIClient.pickerAppId)\""
        return """
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"><title>FileFluss — choose folders</title></head>
        <body style="font-family:-apple-system,sans-serif;padding:24px;color:#333;">
        <p id="status">Loading Google Picker… If nothing appears, make sure you're signed in to Google in this browser.</p>
        <script src="https://apis.google.com/js/api.js"></script>
        <script>
          const TOKEN = \(tokenJSON);
          const APIKEY = \(keyJSON);
          const APPID = \(appIdJSON);
          function report(action, docs){
            const p = new URLSearchParams();
            p.set('action', action);
            if (docs) p.set('data', JSON.stringify(docs));
            fetch('/result?' + p.toString()).then(function(){
              document.getElementById('status').textContent = 'Done — you can close this tab and return to FileFluss.';
            });
          }
          function onApiLoad(){ gapi.load('picker', { callback: createPicker }); }
          function createPicker(){
            try {
              document.getElementById('status').textContent = 'Browse into My Drive, select the folders FileFluss may access, then click Select.';
              // A navigable Drive view (My Drive → subfolders) showing folders
              // only, instead of ViewId.FOLDERS which dumps every folder into a
              // flat, unordered list. Lets users drill the hierarchy and keeps
              // name-ordered results.
              const view = new google.picker.DocsView(google.picker.ViewId.DOCS)
                  .setIncludeFolders(true)
                  .setSelectFolderEnabled(true)
                  .setMimeTypes('application/vnd.google-apps.folder')
                  .setParent('root');
              const picker = new google.picker.PickerBuilder()
                  .enableFeature(google.picker.Feature.MULTISELECT_ENABLED)
                  .setAppId(APPID)
                  .setOAuthToken(TOKEN)
                  .setDeveloperKey(APIKEY)
                  .addView(view)
                  .setTitle('Select folders for FileFluss')
                  .setCallback(function(data){
                    const action = data[google.picker.Response.ACTION];
                    if (action == google.picker.Action.PICKED){
                      const docs = (data[google.picker.Response.DOCUMENTS]||[]).map(function(d){
                        return { id: d[google.picker.Document.ID], name: d[google.picker.Document.NAME] };
                      });
                      report('picked', docs);
                    } else if (action == google.picker.Action.CANCEL){
                      report('cancel', null);
                    }
                  })
                  .build();
              picker.setVisible(true);
            } catch(e){ document.getElementById('status').textContent = 'Picker error: ' + e; }
          }
          window.onload = onApiLoad;
        </script>
        </body>
        </html>
        """
    }
}

/// Single-shot guard so the listener's racing callbacks never resume twice.
private final class PickerContinuationGuard: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<[GoogleDrivePickedRoot], Error>?

    func setContinuation(_ c: CheckedContinuation<[GoogleDrivePickedRoot], Error>) {
        lock.lock(); defer { lock.unlock() }
        continuation = c
    }
    func resume(returning value: [GoogleDrivePickedRoot]) {
        lock.lock(); defer { lock.unlock() }
        continuation?.resume(returning: value); continuation = nil
    }
    func resume(throwing error: Error) {
        lock.lock(); defer { lock.unlock() }
        continuation?.resume(throwing: error); continuation = nil
    }
}
