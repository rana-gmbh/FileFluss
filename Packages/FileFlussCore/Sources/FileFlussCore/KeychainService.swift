import Foundation
import Security
import os

private let keychainLog = Logger(subsystem: "com.rana.FileFluss", category: "keychain")

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

public enum KeychainService {
    private static let serviceName = "com.rana-gmbh.FileFluss"

    /// Legacy login keychain. macOS Release builds signed with Developer ID
    /// persist items here reliably across process restarts. The
    /// data-protection keychain (`kSecUseDataProtectionKeychain: true`) was
    /// briefly tried in 1.1 to dodge the admin-password prompt on ad-hoc
    /// re-signed dev builds; turns out save returned errSecSuccess but the
    /// items didn't survive a process restart, breaking add-account on
    /// every release-build relaunch. Debug builds don't hit either path —
    /// they short-circuit to `DevCredentialStore` below.
    private static func baseQuery(key: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: serviceName,
            kSecAttrAccount as String: key,
        ]
    }

    public static func save(key: String, data: Data) throws {
        #if DEBUG
        try DevCredentialStore.save(key: key, data: data)
        return
        #else
        var query = baseQuery(key: key)
        SecItemDelete(query as CFDictionary)
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw KeychainError.saveFailed(status)
        }
        #endif
    }

    public static func load(key: String) -> Data? {
        #if DEBUG
        if let data = DevCredentialStore.load(key: key) {
            return data
        }
        // One-shot pull-from-keychain for items left over by older dev
        // builds that wrote to the system keychain. Copies the value into
        // the dev folder so future reads are silent. macOS may prompt
        // once per account during this pass when the original keychain
        // ACL points at a previous code signature.
        var query = baseQuery(key: key)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecSuccess, let data = result as? Data {
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
        guard status == errSecSuccess, let data = result as? Data else {
            return nil
        }
        return data
        #endif
    }

    public static func delete(key: String) throws {
        #if DEBUG
        try DevCredentialStore.delete(key: key)
        return
        #else
        let status = SecItemDelete(baseQuery(key: key) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.deleteFailed(status)
        }
        #endif
    }

    public static func save<T: Encodable>(key: String, value: T) throws {
        let data = try JSONEncoder().encode(value)
        try save(key: key, data: data)
    }

    public static func load<T: Decodable>(key: String, as type: T.Type) -> T? {
        guard let data = load(key: key) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
}

public enum KeychainError: LocalizedError {
    case saveFailed(OSStatus)
    case deleteFailed(OSStatus)

    public var errorDescription: String? {
        switch self {
        case .saveFailed(let status):
            return "Keychain save failed (OSStatus \(status))"
        case .deleteFailed(let status):
            return "Keychain delete failed (OSStatus \(status))"
        }
    }
}
