import SwiftUI
import FileFlussCore

/// Options sheet for "Share Link…". Only renders the controls the
/// provider actually supports — `ShareLinkCapabilities` is the single
/// source of truth, so the user never sets a password on a provider
/// that would reject it.
struct ShareLinkSheet: View {
    let fileName: String
    let capabilities: ShareLinkCapabilities
    let onCreate: (ShareLinkOptions) -> Void

    @Environment(\.dismiss) private var dismiss

    @State private var usePassword = false
    @State private var password = ""
    @State private var useExpiry = false
    @State private var expiry = Date().addingTimeInterval(7 * 24 * 60 * 60)
    @State private var allowDownload = true

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
        if usePassword && password.isEmpty { return false }
        return true
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                LText("Create Share Link")
                    .font(.headline)
                Text(fileName)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            VStack(alignment: .leading, spacing: 10) {
                if capabilities.supportsPassword {
                    Toggle(L10n.text("Protect with password"), isOn: $usePassword)
                    if usePassword {
                        SecureField(L10n.text("Password"), text: $password)
                            .textFieldStyle(.roundedBorder)
                            .frame(maxWidth: 260)
                    }
                }

                if capabilities.supportsExpiry {
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

            // Both are commonly paid-plan features, and the request fails
            // outright rather than silently dropping them — say so up front.
            if capabilities.supportsPassword || capabilities.supportsExpiry {
                LText("Some providers offer passwords and expiry dates only on paid plans. If the link can't be created, the provider's own message is shown.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button(L10n.text("Cancel"), role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(L10n.text("Create Link")) {
                    let wantsExpiry = capabilities.supportsExpiry && (useExpiry || capabilities.requiresExpiry)
                    onCreate(ShareLinkOptions(
                        password: usePassword ? password : nil,
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
            if let latest = latestAllowedExpiry, expiry > latest {
                expiry = latest
            }
        }
    }
}
