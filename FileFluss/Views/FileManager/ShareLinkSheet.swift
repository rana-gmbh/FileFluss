import SwiftUI
import FileFlussCore

/// Options sheet for "Share Link…". Only renders the controls the
/// provider actually supports — `ShareLinkCapabilities` is the single
/// source of truth, so the user never sets a password on a provider
/// that would reject it.
struct ShareLinkSheet: View {
    let fileName: String
    let capabilities: ShareLinkCapabilities
    /// The link this file already has, when it is already shared. Its
    /// settings pre-fill the fields, and the wording changes from creating
    /// to changing.
    var existingLink: CloudShareLink?
    /// Name of the provider, for wording like "requires a paid Box plan".
    var providerName: String = ""
    /// Options this account has already refused once. Hidden behind "Show
    /// options this account refused" rather than removed, since a plan can
    /// be upgraded.
    var rejectedPassword: Bool = false
    var rejectedExpiry: Bool = false
    /// True once a link from this account was found to need a sign-in.
    var linksAreNotPublic: Bool = false
    let onCreate: (ShareLinkOptions) -> Void

    @Environment(\.dismiss) private var dismiss

    @State private var usePassword = false
    @State private var password = ""
    @State private var useExpiry = false
    @State private var expiry = Date().addingTimeInterval(7 * 24 * 60 * 60)
    @State private var allowDownload = true
    /// Set by the "show them anyway" button when the user wants to retry an
    /// option this account refused before (e.g. after upgrading a plan).
    @State private var overrideRejected = false

    private var showPassword: Bool {
        capabilities.supportsPassword && (!rejectedPassword || overrideRejected)
    }
    private var showExpiry: Bool {
        capabilities.supportsExpiry && (!rejectedExpiry || overrideRejected)
    }
    private var hasHiddenOptions: Bool {
        (capabilities.supportsPassword && rejectedPassword)
            || (capabilities.supportsExpiry && rejectedExpiry)
    }

    /// One line naming the options this provider bills for, so the user
    /// isn't told only after the request is refused.
    private var paidPlanNote: String? {
        var billed: [String] = []
        if showPassword && capabilities.passwordRequiresPaidPlan { billed.append(L10n.text("password protection")) }
        if showExpiry && capabilities.expiryRequiresPaidPlan { billed.append(L10n.text("expiry dates")) }
        guard !billed.isEmpty else { return nil }
        let list = billed.joined(separator: L10n.text(" and "))
        return providerName.isEmpty
            ? L10n.format("%@ usually need a paid plan.", list)
            : L10n.format("%@ usually need a paid %@ plan.", list, providerName)
    }

    /// S3-style providers sign the link for a fixed window, so expiry is
    /// mandatory and capped — the picker must not offer a date the
    /// provider would refuse to sign.
    private var latestAllowedExpiry: Date? {
        capabilities.maximumExpiry.map { Date().addingTimeInterval($0) }
    }

    private var expiryRange: ClosedRange<Date> {
        let earliest = Date().addingTimeInterval(60)
        let latest = latestAllowedExpiry ?? Date().addingTimeInterval(365 * 24 * 60 * 60)
        return earliest...max(earliest, latest)
    }

    private var canCreate: Bool {
        // When changing an existing password-protected link, an empty field
        // means "keep the current password", so it is allowed.
        if showPassword && usePassword && password.isEmpty && existingLink?.hasPassword != true { return false }
        return true
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                LText(existingLink == nil ? "Create Share Link" : "Change Sharing")
                    .font(.headline)
                Text(fileName)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            VStack(alignment: .leading, spacing: 10) {
                if showPassword {
                    Toggle(L10n.text("Protect with password"), isOn: $usePassword)
                    if usePassword {
                        SecureField(L10n.text("Password"), text: $password)
                            .textFieldStyle(.roundedBorder)
                            .frame(maxWidth: 260)
                        if existingLink?.hasPassword == true {
                            // Providers never hand a password back, so the
                            // field starts empty even though one is set.
                            LText("This link already has a password. Type a new one to replace it.")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                }

                if showExpiry {
                    if capabilities.requiresExpiry {
                        // Not optional here — just show the picker.
                        DatePicker(
                            L10n.text("Expires"),
                            selection: $expiry,
                            in: expiryRange,
                            displayedComponents: [.date, .hourAndMinute]
                        )
                    } else {
                        Toggle(L10n.text("Set an expiry date"), isOn: $useExpiry)
                        if useExpiry {
                            DatePicker(
                                L10n.text("Expires"),
                                selection: $expiry,
                                in: expiryRange,
                                displayedComponents: [.date, .hourAndMinute]
                            )
                        }
                    }
                }

                if capabilities.supportsDownloadToggle {
                    Toggle(L10n.text("Allow downloading"), isOn: $allowDownload)
                }
            }

            if linksAreNotPublic {
                HStack(alignment: .top, spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                    LText("Links from this account weren't publicly reachable last time — opening one asked for a sign-in. The file itself is shared, but only people with an account can open it.")
                        .fixedSize(horizontal: false, vertical: true)
                }
                .font(.caption)
            }

            if let paidPlanNote {
                Text(paidPlanNote)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if hasHiddenOptions && !overrideRejected {
                VStack(alignment: .leading, spacing: 4) {
                    LText("This account refused a password or expiry date before, so those options are hidden.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Button(L10n.text("Show them anyway")) { overrideRejected = true }
                        .buttonStyle(.link)
                        .font(.caption)
                }
            }

            HStack {
                Spacer()
                Button(L10n.text("Cancel"), role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(existingLink == nil ? L10n.text("Create Link") : L10n.text("Save Changes")) {
                    let wantsExpiry = showExpiry && (useExpiry || capabilities.requiresExpiry)
                    onCreate(ShareLinkOptions(
                        password: (showPassword && usePassword) ? password : nil,
                        expiry: wantsExpiry ? expiry : nil,
                        allowDownload: capabilities.supportsDownloadToggle ? allowDownload : true
                    ))
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!canCreate)
            }
        }
        .padding(20)
        .frame(width: 420)
        .onAppear {
            // Pre-fill from the existing link so the sheet shows the state
            // the user is changing, not an empty form.
            if let existing = existingLink {
                usePassword = existing.hasPassword
                if let expiresAt = existing.expiresAt {
                    useExpiry = true
                    expiry = expiresAt
                }
            }
            if let latest = latestAllowedExpiry, expiry > latest {
                expiry = latest
            }
        }
    }
}
