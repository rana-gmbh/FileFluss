import Foundation
import CryptoKit

final class PCloudProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .pCloud

    private var apiClient: PCloudAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "pcloud.\(accountId.uuidString)"
        restoreCredentials()
    }

    init(credentials: PCloudCredentials) {
        self.keychainKey = "pcloud.\(credentials.userId)"
        self.apiClient = PCloudAPIClient(credentials: credentials)
    }

    // MARK: - Authentication

    func authenticate(email: String, password: String) async throws {
        let credentials = try await loginWithPassword(email: email, password: password)
        self.apiClient = PCloudAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
    }

    func authenticate(accessToken: String) async throws {
        let token = accessToken.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !token.isEmpty else { throw CloudProviderError.unauthorized }
        let credentials = try await validateToken(token)
        self.apiClient = PCloudAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
    }

    func authenticate() async throws {
        throw CloudProviderError.notAuthenticated
    }

    func disconnect() async throws {
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    func userDisplayName() async throws -> String {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let info = try await client.userInfo()
        return info.email
    }

    // MARK: - File Operations

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.listFolder(path: path)
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let folderPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent
        try await client.uploadFile(from: localURL, toFolder: folderPath, fileName: fileName, onBytes: onBytes)
    }

    func deleteItem(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        do {
            try await client.deleteFile(path: path)
        } catch CloudProviderError.notFound {
            try await client.deleteFolder(path: path)
        }
    }

    func createDirectory(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.createFolder(path: path)
    }

    func renameItem(at path: String, to newName: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let metadata = try await client.stat(path: path)
        if metadata.isDirectory {
            try await client.renameFolder(path: path, toName: newName)
        } else {
            try await client.renameFile(path: path, toName: newName)
        }
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.stat(path: path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(path: path)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: PCloudCredentials.self) {
            apiClient = PCloudAPIClient(credentials: creds)
        }
    }

    // MARK: - Password login

    private static let browserUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15"
    private static let hostnames = ["eapi.pcloud.com", "api.pcloud.com"]

    /// Attempts digest-based login across both regions. pCloud's `/userinfo`
    /// now authenticates the request but refuses to issue an `auth` token
    /// unless the caller is an OAuth client — and pCloud has suspended new
    /// OAuth app registrations. If the server says "yes you're you" without
    /// handing back a token, we surface a dedicated error so the UI can
    /// guide the user to paste their `pcauth` cookie instead.
    private func loginWithPassword(email: String, password: String) async throws -> PCloudCredentials {
        var lastError: Error = CloudProviderError.unauthorized
        var authWithheld = false
        var credentialsValid = false

        for hostname in Self.hostnames {
            let digest: String
            do {
                digest = try await fetchDigest(hostname: hostname)
            } catch {
                lastError = error
                continue
            }
            let passwordDigest = Self.pcloudPasswordDigest(email: email, password: password, digest: digest)

            let queryItems: [URLQueryItem] = [
                URLQueryItem(name: "getauth", value: "1"),
                URLQueryItem(name: "authexpire", value: "31536000"),
                URLQueryItem(name: "username", value: email),
                URLQueryItem(name: "digest", value: digest),
                URLQueryItem(name: "passworddigest", value: passwordDigest),
            ]

            do {
                return try await submitLogin(hostname: hostname, queryItems: queryItems)
            } catch PCloudLoginError.tokenWithheld {
                authWithheld = true
                credentialsValid = true
                continue
            } catch CloudProviderError.unauthorized {
                continue
            } catch CloudProviderError.invalidCredentials {
                throw CloudProviderError.invalidCredentials
            } catch {
                lastError = error
                continue
            }
        }

        if authWithheld && credentialsValid {
            throw CloudProviderError.serverError(1022)
        }
        throw lastError
    }

    private enum PCloudLoginError: Error {
        case tokenWithheld
    }

    private func submitLogin(
        hostname: String,
        queryItems: [URLQueryItem]
    ) async throws -> PCloudCredentials {
        var components = URLComponents(string: "https://\(hostname)/userinfo")!
        components.queryItems = queryItems
        guard let url = components.url else { throw CloudProviderError.invalidResponse }

        var request = URLRequest(url: url)
        request.setValue(Self.browserUserAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError(0)
        }

        struct LoginResponse: Decodable {
            let result: Int
            let auth: String?
            let userid: UInt64?
            let email: String?
            let error: String?
        }

        let decoded: LoginResponse
        do {
            decoded = try JSONDecoder().decode(LoginResponse.self, from: data)
        } catch {
            throw CloudProviderError.invalidResponse
        }

        switch decoded.result {
        case 0:
            if let auth = decoded.auth, !auth.isEmpty, let uid = decoded.userid {
                return PCloudCredentials(accessToken: auth, hostname: hostname, userId: uid)
            }
            // Authenticated, but pCloud silently dropped the auth token —
            // the OAuth-only restriction for third-party clients.
            throw PCloudLoginError.tokenWithheld
        case 2000, 2012:
            throw CloudProviderError.unauthorized
        case 2205, 2297:
            throw CloudProviderError.invalidCredentials
        default:
            throw CloudProviderError.serverError(decoded.result)
        }
    }

    private func fetchDigest(hostname: String) async throws -> String {
        guard let url = URL(string: "https://\(hostname)/getdigest") else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.setValue(Self.browserUserAgent, forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError(0)
        }
        struct DigestResponse: Decodable {
            let result: Int
            let digest: String?
        }
        guard let decoded = try? JSONDecoder().decode(DigestResponse.self, from: data),
              decoded.result == 0, let digest = decoded.digest else {
            throw CloudProviderError.invalidResponse
        }
        return digest
    }

    private static func pcloudPasswordDigest(email: String, password: String, digest: String) -> String {
        let emailLower = email.lowercased()
        let emailSha1Hex = sha1Hex(Data(emailLower.utf8))
        var input = Data()
        input.append(Data(password.utf8))
        input.append(Data(emailSha1Hex.utf8))
        input.append(Data(digest.utf8))
        return sha1Hex(input)
    }

    private static func sha1Hex(_ data: Data) -> String {
        Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Access token fallback

    private func validateToken(_ token: String) async throws -> PCloudCredentials {
        for hostname in Self.hostnames {
            var components = URLComponents(string: "https://\(hostname)/userinfo")!
            components.queryItems = [URLQueryItem(name: "auth", value: token)]
            guard let url = components.url else { continue }

            var request = URLRequest(url: url)
            request.setValue(Self.browserUserAgent, forHTTPHeaderField: "User-Agent")

            let data: Data
            let response: URLResponse
            do {
                (data, response) = try await URLSession.shared.data(for: request)
            } catch {
                continue
            }
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                continue
            }

            struct TokenResponse: Decodable {
                let result: Int
                let userid: UInt64?
                let email: String?
            }
            guard let decoded = try? JSONDecoder().decode(TokenResponse.self, from: data) else {
                continue
            }
            if decoded.result == 0, let userId = decoded.userid {
                return PCloudCredentials(accessToken: token, hostname: hostname, userId: userId)
            }
        }

        throw CloudProviderError.invalidCredentials
    }
}
