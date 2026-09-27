import Foundation

/// What the user asked for when creating a public share link. Providers
/// ignore the fields their API doesn't support — `ShareLinkCapabilities`
/// tells the UI which ones to offer in the first place, so an ignored
/// field should never reach a provider that can't honour it.
public struct ShareLinkOptions: Sendable, Equatable {
    /// Password the recipient must enter. Most providers gate this behind
    /// a paid plan and reject the whole request when it isn't available.
    public var password: String?
    /// When the link stops working. S3-style presigned links *require*
    /// one (and cap it at 7 days); most others treat nil as "never".
    public var expiry: Date?
    /// False asks the provider to allow viewing but not downloading.
    /// Only honoured where `ShareLinkCapabilities.supportsDownloadToggle`.
    public var allowDownload: Bool

    public init(password: String? = nil, expiry: Date? = nil, allowDownload: Bool = true) {
        self.password = password
        self.expiry = expiry
        self.allowDownload = allowDownload
    }

    public static let `default` = ShareLinkOptions()
}

/// A public link created for a remote file.
public struct CloudShareLink: Sendable, Equatable {
    /// The provider's landing page — what the user would get from the web UI.
    public let url: URL
    /// A URL that serves the file bytes directly, where the provider offers
    /// one. Nil for landing-page-only providers (OneDrive) and for
    /// end-to-end-encrypted ones, where no plain URL can exist.
    public let directDownloadURL: URL?
    /// Effective expiry as reported back by the server, which may differ
    /// from what was requested (or be nil when the link never expires).
    public let expiresAt: Date?
    /// Whether the created link actually ended up password-protected.
    public let hasPassword: Bool
    /// False when the link does NOT work for just anyone — e.g. OneDrive
    /// tenants that forbid anonymous links hand back an organisation-only
    /// link, and Box admins can downgrade a link to collaborators. The user
    /// has to be told: a link that silently needs a sign-in looks identical
    /// to a public one until the recipient hits a login page.
    public let isPublic: Bool
    /// Optional note worth surfacing to the user — e.g. when the server
    /// granted less than we asked for.
    public let note: String?

    public init(
        url: URL,
        directDownloadURL: URL? = nil,
        expiresAt: Date? = nil,
        hasPassword: Bool = false,
        isPublic: Bool = true,
        note: String? = nil
    ) {
        self.url = url
        self.directDownloadURL = directDownloadURL
        self.expiresAt = expiresAt
        self.hasPassword = hasPassword
        self.isPublic = isPublic
        self.note = note
    }

    /// The URL the app copies to the clipboard. The user preference is
    /// "direct download when available", so prefer that and fall back to
    /// the landing page.
    public var preferredURL: URL { directDownloadURL ?? url }
}

/// Static description of what a provider's sharing API can do. Drives
/// which menu items and sheet fields are offered — the app never shows a
/// control for something the provider would reject.
///
/// Note that this is a *ceiling*, not a guarantee: a Nextcloud admin can
/// switch link sharing off entirely, and password/expiry are paid-plan
/// features at Dropbox, Box, pCloud and kDrive. So `createShareLink` can
/// still fail on a provider that advertises support, and callers must
/// surface the server's own message rather than a generic one.
public struct ShareLinkCapabilities: Sendable, Equatable {
    public let canCreate: Bool
    /// The provider can be asked whether a file already has a public link.
    /// Without this the app can only know about links it made itself.
    public let canQueryExisting: Bool
    /// An existing link's password/expiry can be changed in place.
    public let canUpdate: Bool
    /// Sharing can be withdrawn again.
    public let canRemove: Bool
    public let supportsPassword: Bool
    public let supportsExpiry: Bool
    public let supportsDownloadToggle: Bool
    /// These options exist in the provider's API but are billed features,
    /// so an account on a free plan gets the request rejected outright
    /// rather than the option being ignored. The UI says so up front
    /// instead of letting the user discover it through an error.
    public let passwordRequiresPaidPlan: Bool
    public let expiryRequiresPaidPlan: Bool
    /// True when the provider *requires* an expiry date (S3 presigned
    /// URLs, which are signed for a fixed window).
    public let requiresExpiry: Bool
    /// Longest expiry the provider accepts, in seconds. Nil means no known
    /// cap. S3 SigV4 signing tops out at 7 days.
    public let maximumExpiry: TimeInterval?

    public init(
        canCreate: Bool,
        canQueryExisting: Bool = false,
        canUpdate: Bool = false,
        canRemove: Bool = false,
        supportsPassword: Bool = false,
        supportsExpiry: Bool = false,
        supportsDownloadToggle: Bool = false,
        passwordRequiresPaidPlan: Bool = false,
        expiryRequiresPaidPlan: Bool = false,
        requiresExpiry: Bool = false,
        maximumExpiry: TimeInterval? = nil
    ) {
        self.canCreate = canCreate
        self.canQueryExisting = canQueryExisting
        self.canUpdate = canUpdate
        self.canRemove = canRemove
        self.supportsPassword = supportsPassword
        self.supportsExpiry = supportsExpiry
        self.supportsDownloadToggle = supportsDownloadToggle
        self.passwordRequiresPaidPlan = passwordRequiresPaidPlan
        self.expiryRequiresPaidPlan = expiryRequiresPaidPlan
        self.requiresExpiry = requiresExpiry
        self.maximumExpiry = maximumExpiry
    }

    /// Providers that have no sharing API at all (SFTP, FTP, plain WebDAV,
    /// GMX, TeraBox, iCloud Drive, GoPro).
    public static let unsupported = ShareLinkCapabilities(canCreate: false)

    /// True when the options sheet has anything to offer; otherwise the
    /// menu only shows the one-click "Copy Share Link".
    public var hasOptions: Bool {
        supportsPassword || supportsExpiry || supportsDownloadToggle
    }
}
