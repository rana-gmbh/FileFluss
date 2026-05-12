import Foundation
import Security

#if DEBUG
/// Dev-only credential store: a 0600-permissions JSON-per-key folder
/// under `~/Library/Application Support/com.rana-gmbh.FileFluss/dev-credentials/`.
/// Ad-hoc-signed debug builds get a new code-signing identity on every
/// rebuild, which makes the macOS keychain prompt the developer every
/// single time. This sidesteps that — RELEASE builds still use the real
/// keychain. Not for production use.
private enum DevCredentialStore {
    static let directory: URL = {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = appSupport.appendingPathComponent("com.rana-gmbh.FileFluss/dev-credentials", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: dir,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        return dir
    }()

    static func save(key: String, data: Data) throws {
        let url = file(for: key)
        try data.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: url.path
        )
    }

    static func load(key: String) -> Data? {
        try? Data(contentsOf: file(for: key))
    }

    static func delete(key: String) throws {
        let url = file(for: key)
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        try FileManager.default.removeItem(at: url)
    }

    private static func file(for key: String) -> URL {
        // Keep keys filesystem-safe — providers use UUID-style strings so
        // this is mostly defensive.
        let safe = key.replacingOccurrences(of: "/", with: "_")
        return directory.appendingPathComponent("\(safe).bin")
    }
}
#endif

enum KeychainService {
    private static let serviceName = "com.rana-gmbh.FileFluss"

    /// Modern data-protection keychain. Sandboxed per-bundle-id rather
    /// than per-code-signature, so ad-hoc-signed dev builds don't trigger
    /// the "FileFluss wants to access the keychain" prompt every rebuild
    /// the way the legacy keychain does.
    private static func baseQuery(key: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: serviceName,
            kSecAttrAccount as String: key,
            kSecUseDataProtectionKeychain as String: true,
        ]
    }

    /// Legacy file-keychain query — only used during the one-time
    /// migration on first read after upgrading to the data-protection
    /// store. Once an item is migrated, the legacy copy is deleted.
    private static func legacyQuery(key: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: serviceName,
            kSecAttrAccount as String: key,
        ]
    }

    static func save(key: String, data: Data) throws {
        #if DEBUG
        try DevCredentialStore.save(key: key, data: data)
        return
        #else
        // Replace any existing item in the modern store, then write.
        var query = baseQuery(key: key)
        SecItemDelete(query as CFDictionary)
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw KeychainError.saveFailed(status)
        }

        // Best-effort cleanup of any leftover legacy entry under the same
        // key so we don't silently keep two copies in sync.
        SecItemDelete(legacyQuery(key: key) as CFDictionary)
        #endif
    }

    static func load(key: String) -> Data? {
        #if DEBUG
        if let data = DevCredentialStore.load(key: key) {
            return data
        }
        // One-shot migration: read any pre-existing keychain entry left
        // behind by earlier builds and copy it into the dev folder so
        // future reads are silent. macOS will prompt once per account
        // during this pass (because the old keychain ACL points at a
        // previous build's code signature), then never again.
        if let data = loadFromKeychain(key: key) {
            try? DevCredentialStore.save(key: key, data: data)
            return data
        }
        return nil
        #else
        var query = baseQuery(key: key)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecSuccess, let data = result as? Data {
            return data
        }

        // Transparent migration: fall back to the legacy keychain for
        // users upgrading from an earlier build, then move the value into
        // the modern store so future reads hit the fast path.
        return migrateFromLegacy(key: key)
        #endif
    }

    private static func migrateFromLegacy(key: String) -> Data? {
        var legacy = legacyQuery(key: key)
        legacy[kSecReturnData as String] = true
        legacy[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: AnyObject?
        let status = SecItemCopyMatching(legacy as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { return nil }

        try? save(key: key, data: data)
        return data
    }

    /// Reads the keychain (modern data-protection store first, then the
    /// legacy file store) without writing back. Used by the DEBUG-only
    /// migration that copies pre-existing entries into the dev folder.
    private static func loadFromKeychain(key: String) -> Data? {
        var modern = baseQuery(key: key)
        modern[kSecReturnData as String] = true
        modern[kSecMatchLimit as String] = kSecMatchLimitOne
        var modernResult: AnyObject?
        let modernStatus = SecItemCopyMatching(modern as CFDictionary, &modernResult)
        if modernStatus == errSecSuccess, let data = modernResult as? Data {
            return data
        }

        var legacy = legacyQuery(key: key)
        legacy[kSecReturnData as String] = true
        legacy[kSecMatchLimit as String] = kSecMatchLimitOne
        var legacyResult: AnyObject?
        let legacyStatus = SecItemCopyMatching(legacy as CFDictionary, &legacyResult)
        if legacyStatus == errSecSuccess, let data = legacyResult as? Data {
            return data
        }
        return nil
    }

    static func delete(key: String) throws {
        #if DEBUG
        try DevCredentialStore.delete(key: key)
        return
        #else
        // Wipe both stores so a re-add starts from a clean slate.
        let modernStatus = SecItemDelete(baseQuery(key: key) as CFDictionary)
        let legacyStatus = SecItemDelete(legacyQuery(key: key) as CFDictionary)
        let acceptable: Set<OSStatus> = [errSecSuccess, errSecItemNotFound]
        if !acceptable.contains(modernStatus) {
            throw KeychainError.deleteFailed(modernStatus)
        }
        if !acceptable.contains(legacyStatus) {
            throw KeychainError.deleteFailed(legacyStatus)
        }
        #endif
    }

    static func save<T: Encodable>(key: String, value: T) throws {
        let data = try JSONEncoder().encode(value)
        try save(key: key, data: data)
    }

    static func load<T: Decodable>(key: String, as type: T.Type) -> T? {
        guard let data = load(key: key) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
}

enum KeychainError: LocalizedError {
    case saveFailed(OSStatus)
    case deleteFailed(OSStatus)

    var errorDescription: String? {
        switch self {
        case .saveFailed(let status):
            return "Keychain save failed (OSStatus \(status))"
        case .deleteFailed(let status):
            return "Keychain delete failed (OSStatus \(status))"
        }
    }
}
