import Foundation

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
        let credentials = try await loginWithCredentials(email: email, password: password)
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
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let folderPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent
        try await client.uploadFile(from: localURL, toFolder: folderPath, fileName: fileName)
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

    private func loginWithCredentials(email: String, password: String) async throws -> PCloudCredentials {
        // Try EU endpoint first, then US — pCloud returns error 2000 if wrong region
        let hostnames = ["eapi.pcloud.com", "api.pcloud.com"]

        for hostname in hostnames {
            do {
                return try await attemptLogin(email: email, password: password, hostname: hostname)
            } catch CloudProviderError.unauthorized {
                continue
            }
        }

        throw CloudProviderError.unauthorized
    }

    private func attemptLogin(email: String, password: String, hostname: String) async throws -> PCloudCredentials {
        var components = URLComponents(string: "https://\(hostname)/userinfo")!
        components.queryItems = [
            URLQueryItem(name: "getauth", value: "1"),
            URLQueryItem(name: "username", value: email),
            URLQueryItem(name: "password", value: password),
        ]

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        let (data, response) = try await URLSession.shared.data(from: url)

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

        let loginResponse = try JSONDecoder().decode(LoginResponse.self, from: data)

        switch loginResponse.result {
        case 0:
            guard let auth = loginResponse.auth, let userId = loginResponse.userid else {
                throw CloudProviderError.invalidResponse
            }
            return PCloudCredentials(
                accessToken: auth,
                hostname: hostname,
                userId: userId
            )
        case 2000:
            throw CloudProviderError.unauthorized
        case 2205:
            throw CloudProviderError.invalidCredentials
        default:
            throw CloudProviderError.serverError(loginResponse.result)
        }
    }
}
