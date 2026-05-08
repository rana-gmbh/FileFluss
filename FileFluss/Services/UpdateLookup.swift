import Foundation

/// Information about a newer release retrieved from GitHub.
struct AvailableUpdate: Equatable, Sendable {
    let version: String
    let releaseNotes: String
    let releasePageURL: URL
    let downloadURL: URL?
}

enum UpdateLookup {
    private static let latestReleaseURL = URL(string: "https://api.github.com/repos/rana-gmbh/filefluss/releases/latest")!

    static func currentVersion(bundle: Bundle = .main) -> String {
        bundle.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
    }

    /// Hits the GitHub Releases API and returns an `AvailableUpdate` only
    /// when the published tag is newer than the running app's version.
    static func fetchLatestUpdate(currentVersion: String = currentVersion()) async throws -> AvailableUpdate? {
        var request = URLRequest(url: latestReleaseURL)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("FileFluss/\(currentVersion)", forHTTPHeaderField: "User-Agent")

        let (data, response) = try await URLSession.shared.data(for: request)
        try validate(response: response)

        let release = try JSONDecoder().decode(GitHubRelease.self, from: data)
        let latestVersion = release.tagName.trimmingCharacters(in: CharacterSet(charactersIn: "v"))
        guard isNewer(latestVersion, than: currentVersion) else { return nil }

        // Prefer the .dmg asset (the macOS installer), fall back to anything.
        let downloadURL = release.assets
            .first(where: { $0.name.lowercased().hasSuffix(".dmg") })
            .flatMap { URL(string: $0.browserDownloadURL) }
            ?? release.assets.first.flatMap { URL(string: $0.browserDownloadURL) }

        return AvailableUpdate(
            version: latestVersion,
            releaseNotes: release.body ?? "",
            releasePageURL: URL(string: release.htmlURL)!,
            downloadURL: downloadURL
        )
    }

    private static func validate(response: URLResponse) throws {
        guard let httpResponse = response as? HTTPURLResponse else { return }
        guard (200..<300).contains(httpResponse.statusCode) else {
            throw UpdateLookupError.invalidResponse(httpResponse.statusCode)
        }
    }

    /// Component-wise numeric compare, e.g. "1.0.1" > "1.0".
    static func isNewer(_ latest: String, than current: String) -> Bool {
        let latestParts = latest.split(separator: ".").compactMap { Int($0) }
        let currentParts = current.split(separator: ".").compactMap { Int($0) }
        let length = max(latestParts.count, currentParts.count)
        let paddedLatest = latestParts + Array(repeating: 0, count: length - latestParts.count)
        let paddedCurrent = currentParts + Array(repeating: 0, count: length - currentParts.count)

        for (latestValue, currentValue) in zip(paddedLatest, paddedCurrent) {
            if latestValue > currentValue { return true }
            if latestValue < currentValue { return false }
        }
        return false
    }
}

enum UpdateLookupError: LocalizedError {
    case invalidResponse(Int)

    var errorDescription: String? {
        switch self {
        case .invalidResponse(let statusCode):
            return "GitHub returned HTTP \(statusCode)."
        }
    }
}

private struct GitHubRelease: Decodable {
    let tagName: String
    let body: String?
    let htmlURL: String
    let assets: [Asset]

    struct Asset: Decodable {
        let name: String
        let browserDownloadURL: String
        enum CodingKeys: String, CodingKey {
            case name
            case browserDownloadURL = "browser_download_url"
        }
    }

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case body
        case htmlURL = "html_url"
        case assets
    }
}
