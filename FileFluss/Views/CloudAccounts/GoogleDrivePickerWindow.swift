import AppKit
import Network
import Foundation
import os

private let pickerLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDrivePicker")

/// Opens the Google Drive Picker in the user's system browser and captures the
/// result via a localhost HTTP callback — the same pattern used for OAuth.
///
/// WKWebView cannot host the Picker because WebKit's ITP blocks the third-party
/// cookies Google's Picker iframe requires. The system browser already has the
/// user's Google session cookies, so the Picker works out of the box.
@MainActor
final class GoogleDrivePickerWindow: NSObject {
    private var listener: NWListener?
    private var continuation: CheckedContinuation<[PickedDriveFolder], Error>?
    private var htmlData: Data?

    private let accessToken: String
    private let apiKey: String
    private let preselectFileIds: [String]

    init(accessToken: String, apiKey: String, preselectFileIds: [String] = []) {
        self.accessToken = accessToken
        self.apiKey = apiKey
        self.preselectFileIds = preselectFileIds
    }

    func present() async throws -> [PickedDriveFolder] {
        if apiKey.isEmpty {
            throw GoogleDrivePickerError.missingApiKey
        }

        guard let htmlURL = Bundle.main.url(
            forResource: "picker",
            withExtension: "html",
            subdirectory: "GoogleDrivePicker"
        ) ?? Bundle.main.url(forResource: "picker", withExtension: "html"),
              let data = try? Data(contentsOf: htmlURL) else {
            throw GoogleDrivePickerError.bundleResourceMissing
        }
        self.htmlData = data

        return try await withCheckedThrowingContinuation { cont in
            self.continuation = cont
            self.startServer()
        }
    }

    private func startServer() {
        do {
            let listener = try NWListener(using: .tcp, on: .any)
            self.listener = listener

            listener.newConnectionHandler = { [weak self] connection in
                self?.handleConnection(connection)
            }
            listener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready:
                    guard let port = listener.port?.rawValue else { return }
                    Task { @MainActor in
                        self?.openBrowser(port: port)
                    }
                case .failed(let error):
                    Task { @MainActor in
                        self?.finish(throwing: error)
                    }
                default:
                    break
                }
            }
            listener.start(queue: .global(qos: .userInitiated))
        } catch {
            finish(throwing: error)
        }
    }

    private func openBrowser(port: UInt16) {
        var components = URLComponents()
        components.scheme = "http"
        components.host = "127.0.0.1"
        components.port = Int(port)
        components.path = "/picker"
        let projectNumber = GoogleDriveAPIClient.clientId.components(separatedBy: "-").first ?? ""
        components.queryItems = [
            URLQueryItem(name: "token", value: accessToken),
            URLQueryItem(name: "apiKey", value: apiKey),
            URLQueryItem(name: "appId", value: projectNumber),
            URLQueryItem(name: "port", value: String(port)),
        ]
        if !preselectFileIds.isEmpty {
            components.queryItems?.append(
                URLQueryItem(name: "preselect", value: preselectFileIds.joined(separator: ","))
            )
        }
        if let url = components.url {
            NSWorkspace.shared.open(url)
        }
    }

    private nonisolated func handleConnection(_ connection: NWConnection) {
        connection.start(queue: .global(qos: .userInitiated))
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, _, _ in
            guard let self, let data, let request = String(data: data, encoding: .utf8) else {
                connection.cancel()
                return
            }

            guard let firstLine = request.components(separatedBy: "\r\n").first,
                  let urlPart = firstLine.split(separator: " ").dropFirst().first,
                  let components = URLComponents(string: "http://localhost\(urlPart)") else {
                connection.cancel()
                return
            }

            let path = components.path

            if path == "/callback" {
                let params = components.queryItems ?? []
                let action = params.first(where: { $0.name == "action" })?.value ?? ""
                let foldersJSON = params.first(where: { $0.name == "folders" })?.value

                let doneHTML = Data("""
                <!DOCTYPE html><html><head><meta charset="utf-8"></head><body style="font-family:-apple-system,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#f5f5f7">
                <div style="text-align:center"><div style="font-size:48px;color:#34c759">&#10003;</div><p>Done &#8212; you can close this tab.</p></div>
                </body></html>
                """.utf8)
                let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(doneHTML.count)\r\nConnection: close\r\n\r\n"
                var responseData = Data(response.utf8)
                responseData.append(doneHTML)
                connection.send(content: responseData, completion: .contentProcessed { _ in
                    connection.cancel()
                })

                Task { @MainActor in
                    if action == "picked", let json = foldersJSON,
                       let jsonData = json.removingPercentEncoding?.data(using: .utf8),
                       let raw = try? JSONSerialization.jsonObject(with: jsonData) as? [[String: Any]] {
                        let folders: [PickedDriveFolder] = raw.compactMap { d in
                            guard let id = d["id"] as? String,
                                  let name = d["name"] as? String,
                                  (d["mimeType"] as? String) == "application/vnd.google-apps.folder" else {
                                return nil
                            }
                            return PickedDriveFolder(id: id, name: name)
                        }
                        pickerLog.info("Picker returned \(folders.count) folder(s)")
                        self.finish(returning: folders)
                    } else {
                        pickerLog.info("Picker cancelled by user")
                        self.finish(returning: [])
                    }
                }
            } else {
                guard let htmlData = self.htmlData else {
                    connection.cancel()
                    return
                }
                let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(htmlData.count)\r\nConnection: close\r\n\r\n"
                var responseData = Data(response.utf8)
                responseData.append(htmlData)
                connection.send(content: responseData, completion: .contentProcessed { _ in
                    connection.cancel()
                })
            }
        }
    }

    private func finish(returning folders: [PickedDriveFolder]) {
        guard let cont = continuation else { return }
        continuation = nil
        cont.resume(returning: folders)
        stopServer()
    }

    private func finish(throwing error: Error) {
        guard let cont = continuation else { return }
        continuation = nil
        cont.resume(throwing: error)
        stopServer()
    }

    private func stopServer() {
        listener?.cancel()
        listener = nil
        htmlData = nil
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
