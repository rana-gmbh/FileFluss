import Foundation

/// Editable snapshot of an existing cloud account's credentials, used by
/// the Edit Account sheet to pre-populate fields. Only the credential
/// pieces we want to show in the UI are surfaced — passwords and tokens
/// that the user has to retype (per the product decision in issue
/// follow-up) stay out of this struct.
///
/// Lives in FileFlussCore so it can read the internal-access fields of
/// each `*Credentials` struct without us having to promote those to
/// `public` (which would leak secrets like API tokens through the type
/// system).
public struct CloudAccountEditSnapshot: Sendable {
    public enum SFTPAuth: Sendable {
        case password
        case privateKey
    }

    // Common
    public var email: String = ""
    public var username: String = ""
    public var serverURL: String = ""

    // SFTP
    public var host: String = ""
    public var port: Int = 22
    public var remotePath: String = "/"
    public var sftpAuth: SFTPAuth = .password

    // S3 family
    public var s3AccessKeyId: String = ""
    public var s3SecretAccessKey: String = ""
    public var s3Region: String = ""
    public var s3Endpoint: String = ""
    public var s3DisplayName: String = ""

    // Self-signed cert toggles
    public var allowSelfSignedCertificate: Bool = false

    public init() {}
}

public enum CloudAccountEditLoader {
    /// Builds a snapshot from whatever's currently in the keychain for
    /// `accountId`. Returns nil for providers that have nothing
    /// meaningfully editable (Dropbox/Google Drive/OneDrive/Box — pure
    /// OAuth re-auth; pCloud/kDrive — token-only; iCloud — system auth).
    public static func snapshot(
        for accountId: UUID,
        providerType: CloudProviderType
    ) -> CloudAccountEditSnapshot? {
        var snap = CloudAccountEditSnapshot()
        let suffix = accountId.uuidString
        switch providerType {
        case .s3:
            guard let c = KeychainService.load(key: "s3.\(suffix)", as: S3Credentials.self) else { return nil }
            snap.s3AccessKeyId = c.accessKeyId
            snap.s3SecretAccessKey = c.secretAccessKey
            snap.s3Region = c.region
            return snap

        case .s3Compatible:
            guard let c = KeychainService.load(key: "s3Compatible.\(suffix)", as: S3Credentials.self) else { return nil }
            snap.s3AccessKeyId = c.accessKeyId
            snap.s3SecretAccessKey = c.secretAccessKey
            snap.s3Region = c.region
            snap.s3Endpoint = c.endpointHost ?? ""
            snap.s3DisplayName = c.displayName
            return snap

        case .synologyC2:
            guard let c = KeychainService.load(key: "synologyC2.\(suffix)", as: S3Credentials.self) else { return nil }
            snap.s3AccessKeyId = c.accessKeyId
            snap.s3SecretAccessKey = c.secretAccessKey
            // For Synology C2 the "endpoint URL" the user originally
            // pasted lives in endpointHost; surface it back into the
            // Region field where the Add form expects it.
            snap.s3Region = c.endpointHost ?? c.region
            return snap

        case .sftp:
            guard let c = KeychainService.load(key: "sftp.\(suffix)", as: SFTPCredentials.self) else { return nil }
            snap.host = c.host
            snap.port = c.port
            snap.username = c.username
            snap.remotePath = c.remotePath
            snap.sftpAuth = c.authMethod == .privateKey ? .privateKey : .password
            return snap

        case .webDAV:
            guard let c = KeychainService.load(key: "webdav.\(suffix)", as: WebDAVCredentials.self) else { return nil }
            snap.serverURL = c.serverURL
            snap.username = c.username
            return snap

        case .gmxCloud:
            guard let c = KeychainService.load(key: "gmxCloud.\(suffix)", as: WebDAVCredentials.self) else { return nil }
            // GMX Cloud presents as email + password in the Add form,
            // not server URL + username.
            snap.email = c.username
            return snap

        case .nextCloud:
            guard let c = KeychainService.load(key: "nextcloud.\(suffix)", as: NextCloudCredentials.self) else { return nil }
            snap.serverURL = c.serverURL
            snap.username = c.username
            return snap

        case .synologyDrive:
            guard let c = KeychainService.load(key: "synologyDrive.\(suffix)", as: SynologyDriveCredentials.self) else { return nil }
            snap.serverURL = c.serverURL
            snap.username = c.username
            snap.allowSelfSignedCertificate = c.allowSelfSignedCertificate
            return snap

        case .wordpress:
            guard let c = KeychainService.load(key: "wordpress.\(suffix)", as: WordPressCredentials.self) else { return nil }
            snap.serverURL = c.siteURL
            snap.username = c.username
            return snap

        case .seafile:
            guard let c = KeychainService.load(key: "seafile.\(suffix)", as: SeafileCredentials.self) else { return nil }
            snap.serverURL = c.serverURL
            snap.email = c.username
            snap.allowSelfSignedCertificate = c.allowSelfSignedCertificate
            return snap

        case .koofr:
            guard let c = KeychainService.load(key: "koofr.\(suffix)", as: KoofrCredentials.self) else { return nil }
            snap.email = c.email
            return snap

        case .mega:
            guard let c = KeychainService.load(key: "mega.\(suffix)", as: MegaCredentials.self) else { return nil }
            snap.email = c.email
            return snap

        case .filen:
            guard let c = KeychainService.load(key: "filen.\(suffix)", as: FilenCredentials.self) else { return nil }
            snap.email = c.email
            return snap

        case .dropbox, .googleDrive, .oneDrive, .box, .pCloud, .kDrive, .iCloud:
            return nil
        }
    }
}
