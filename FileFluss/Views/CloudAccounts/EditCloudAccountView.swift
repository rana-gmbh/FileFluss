import SwiftUI
import FileFlussCore
import AppKit
import FileFlussCore

/// Edit-credential sheet for an existing cloud account. Mirrors the
/// add flow's per-provider forms but locks the provider type, pre-
/// populates fields from the keychain where possible, and disables
/// Save until the user actually changes something. Saving re-uses the
/// existing accountId so sync rules, panel state, and favourites stay
/// intact — only the keychain entry and the SyncEngine provider for
/// this account are replaced.
struct EditCloudAccountView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    let account: CloudAccount

    @State private var didConfigureWindow = false
    @State private var isAuthenticating = false
    @State private var saveTask: Task<Void, Never>?

    // Captured prefill so Save can diff against it.
    @State private var initial = CloudAccountEditSnapshot()
    @State private var didPrime = false

    // Generic editable fields. Each provider uses the subset that
    // applies. Keeping them in one struct mirrors how the Add form is
    // organised and makes the diff check straightforward.
    @State private var email = ""
    @State private var password = ""
    @State private var apiToken = ""
    @State private var serverURL = ""
    @State private var username = ""
    @State private var port = "22"
    @State private var remotePath = "/"

    @State private var sftpAuthMethod: AddCloudAccountView.SFTPAuthMethod = .password
    @State private var sftpPassphrase = ""
    @State private var sftpPrivateKeyContents = ""
    @State private var sftpPrivateKeyFilename = ""
    @State private var sftpKeyImporterPresented = false
    @State private var sftpKeyImportError: String?

    @State private var s3AccessKeyId = ""
    @State private var s3SecretAccessKey = ""
    @State private var s3Region = "us-east-1"
    @State private var s3Path = ""
    @State private var s3Endpoint = ""
    @State private var s3DisplayName = ""

    @State private var synologyOTP = ""
    @State private var ftpUseTLS = false
    @State private var ftpAllowSelfSigned = false
    @State private var synologyAllowSelfSigned = true
    @State private var seafileOTP = ""
    @State private var seafileAllowSelfSigned = false
    @State private var filenTwoFactor = ""
    @State private var internxtTwoFactor = ""
    @State private var megaOTP = ""

    @State private var nextCloudMode: AddCloudAccountView.NextCloudAuthMode = .browser

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                header
                form
                if let authError = appState.syncManager.authError {
                    Text(authError)
                        .foregroundStyle(.red)
                        .font(.caption)
                        .multilineTextAlignment(.center)
                }
                actionButtons
            }
            .padding(24)
            .frame(maxWidth: .infinity)
        }
        .frame(minWidth: 460)
        .background(EditWindowConfigurator(didConfigure: $didConfigureWindow))
        .onAppear {
            // Snapshot only on first appearance — subsequent state
            // changes (typed input) shouldn't reset the diff baseline.
            guard !didPrime else { return }
            didPrime = true
            primeFromSnapshot()
        }
        .onDisappear {
            saveTask?.cancel()
            saveTask = nil
            // Edit sheet shares the same syncManager browser-OAuth
            // flags as Add — clear them on dismiss so a half-finished
            // re-auth doesn't pollute the next session.
            appState.syncManager.cancelPendingBrowserAuth()
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            CloudProviderIcon(providerType: account.providerType, size: 24)
            Text(L10n.format("Edit %@", account.displayName))
                .font(.title2.bold())
        }
    }

    // MARK: - Provider-specific form

    @ViewBuilder
    private var form: some View {
        switch account.providerType {
        case .dropbox, .googleDrive, .oneDrive, .box:
            oauthForm
        case .nextCloud:
            nextCloudFields
        case .gmxCloud:
            emailPasswordFields(emailLabel: L10n.text("GMX Email"))
        case .koofr:
            emailPasswordFields(emailLabel: L10n.text("Email"), passwordLabel: L10n.text("App Password"))
        case .mega:
            megaFields
        case .filen:
            filenFields
        case .internxt:
            internxtFields
        case .terabox:
            teraboxHint
        case .ftp:
            ftpFields
        case .seafile:
            seafileFields
        case .webDAV:
            webDAVFields
        case .sftp:
            sftpFields
        case .synologyDrive:
            synologyDriveFields
        case .wordpress:
            wordPressFields
        case .s3:
            s3Fields
        case .s3Compatible:
            s3CompatibleFields
        case .synologyC2:
            synologyC2Fields
        case .pCloud:
            pCloudFields
        case .kDrive:
            kDriveFields
        case .iCloud:
            iCloudHint
        case .jottacloud:
            jottacloudHint
        case .googleDrivePicker:
            googleDrivePickerHint
        }
    }

    private var jottacloudHint: some View {
        LText("To reconnect Jottacloud with a new Personal Login Token, remove this account and add it again.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .multilineTextAlignment(.center)
    }

    private var googleDrivePickerHint: some View {
        LText("To change which folders FileFluss can access, remove this account and add it again, then pick the folders you want.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .multilineTextAlignment(.center)
    }

    private var oauthForm: some View {
        VStack(spacing: 12) {
            let isPendingBrowserAuth = appState.syncManager.isAuthenticatingGoogleDrive
                || appState.syncManager.isAuthenticatingDropbox
                || appState.syncManager.isAuthenticatingOneDrive
                || appState.syncManager.isAuthenticatingBox

            if isPendingBrowserAuth {
                HStack(spacing: 8) {
                    ProgressView().scaleEffect(0.7)
                    LText("Waiting for sign-in in browser…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                LText("Click Re-authorize to sign in again. Your browser will open and the existing token is replaced once you complete the flow.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private func emailPasswordFields(emailLabel: String, passwordLabel: String = L10n.text("Password")) -> some View {
        VStack(spacing: 12) {
            TextField(emailLabel, text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)
            SecureField(passwordLabel, text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { save() }
            LText("Password is not stored locally and is re-entered each time you edit.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private var megaFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
            TextField(L10n.text("Two-factor code (only if enabled)"), text: $megaOTP)
                .textFieldStyle(.roundedBorder)
                .textContentType(.oneTimeCode)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var filenFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
            TextField(L10n.text("Two-factor code (only if enabled)"), text: $filenTwoFactor)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var internxtFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
            TextField(L10n.text("Two-factor code (only if enabled)"), text: $internxtTwoFactor)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var seafileFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Server URL"), text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)
            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
            TextField(L10n.text("Two-factor code (only if enabled)"), text: $seafileOTP)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
            Toggle(L10n.text("Allow self-signed certificate"), isOn: $seafileAllowSelfSigned)
                .disabled(isAuthenticating)
        }
    }

    private var webDAVFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Server URL"), text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)
            TextField(L10n.text("Username"), text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var sftpFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                TextField(L10n.text("Hostname"), text: $serverURL)
                    .textFieldStyle(.roundedBorder)
                    .disabled(isAuthenticating)
                TextField(L10n.text("Port"), text: $port)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 60)
                    .disabled(isAuthenticating)
            }
            TextField(L10n.text("Username"), text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)
            TextField(L10n.text("Remote Path"), text: $remotePath)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            Picker(L10n.text("Authentication"), selection: $sftpAuthMethod) {
                ForEach(AddCloudAccountView.SFTPAuthMethod.allCases) { method in
                    Text(L10n.text(method.rawValue)).tag(method)
                }
            }
            .pickerStyle(.segmented)
            .disabled(isAuthenticating)

            if sftpAuthMethod == .privateKey {
                sftpKeyFields
            } else {
                SecureField(L10n.text("Password"), text: $password)
                    .textFieldStyle(.roundedBorder)
                    .textContentType(.password)
                    .disabled(isAuthenticating)
                    .onSubmit { save() }
            }

            LText("Credentials (password or key) are not prefilled and must be re-entered.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private var sftpKeyFields: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Button {
                    sftpKeyImportError = nil
                    sftpKeyImporterPresented = true
                } label: {
                    Label(sftpPrivateKeyFilename.isEmpty ? L10n.text("Choose Private Key…") : L10n.text("Replace Private Key…"),
                          systemImage: "key.horizontal")
                }
                .disabled(isAuthenticating)
                if !sftpPrivateKeyFilename.isEmpty {
                    Text(sftpPrivateKeyFilename)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }
            if let err = sftpKeyImportError {
                Text(err).font(.caption).foregroundStyle(.red)
            }
            SecureField(L10n.text("Passphrase (leave empty if key has none)"), text: $sftpPassphrase)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
        .fileImporter(
            isPresented: $sftpKeyImporterPresented,
            allowedContentTypes: [.data, .text, .plainText],
            allowsMultipleSelection: false
        ) { result in
            switch result {
            case .success(let urls):
                guard let url = urls.first else { return }
                let didStart = url.startAccessingSecurityScopedResource()
                defer { if didStart { url.stopAccessingSecurityScopedResource() } }
                do {
                    let data = try Data(contentsOf: url)
                    guard let text = String(data: data, encoding: .utf8) else {
                        sftpKeyImportError = L10n.text("Key file is not a UTF-8 text PEM.")
                        return
                    }
                    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard trimmed.hasPrefix("-----BEGIN") else {
                        sftpKeyImportError = L10n.text("That doesn't look like a PEM private key.")
                        return
                    }
                    sftpPrivateKeyContents = text
                    sftpPrivateKeyFilename = url.lastPathComponent
                    sftpKeyImportError = nil
                } catch {
                    sftpKeyImportError = L10n.format("Could not read key file: %@", error.localizedDescription)
                }
            case .failure(let error):
                sftpKeyImportError = error.localizedDescription
            }
        }
    }

    private var synologyDriveFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField(L10n.text("Server URL"), text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)
            TextField(L10n.text("Username"), text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
            TextField(L10n.text("OTP Code (only if 2-factor auth is on)"), text: $synologyOTP)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
            Toggle(L10n.text("Allow self-signed certificate"), isOn: $synologyAllowSelfSigned)
                .disabled(isAuthenticating)
        }
    }

    private var wordPressFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Site URL"), text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)
            TextField(L10n.text("Username"), text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Application Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var s3Fields: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField(L10n.text("Access Key ID"), text: $s3AccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Secret Access Key"), text: $s3SecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            HStack {
                LText("Region").frame(width: 80, alignment: .leading)
                Picker(L10n.text(""), selection: $s3Region) {
                    ForEach(S3RegionList.allRegions, id: \.code) { region in
                        Text("\(region.displayName) (\(region.code))").tag(region.code)
                    }
                }
                .labelsHidden()
                .disabled(isAuthenticating)
            }
            TextField(L10n.text("Or type a custom region code"), text: $s3Region)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            TextField(L10n.text("Path (optional)"), text: $s3Path)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var s3CompatibleFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField(L10n.text("Access Key ID"), text: $s3AccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Secret Access Key"), text: $s3SecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            TextField(L10n.text("Endpoint URL"), text: $s3Endpoint)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            TextField(L10n.text("Region"), text: $s3Region)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            TextField(L10n.text("Display name (optional)"), text: $s3DisplayName)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            TextField(L10n.text("Path (optional)"), text: $s3Path)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var synologyC2Fields: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField(L10n.text("Access Key ID"), text: $s3AccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Secret Access Key"), text: $s3SecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            TextField(L10n.text("Endpoint URL"), text: $s3Region)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var nextCloudFields: some View {
        VStack(spacing: 12) {
            Picker(L10n.text("Sign-in method"), selection: $nextCloudMode) {
                ForEach(AddCloudAccountView.NextCloudAuthMode.allCases) { mode in
                    Text(L10n.text(mode.rawValue)).tag(mode)
                }
            }
            .pickerStyle(.segmented)
            .disabled(isAuthenticating || appState.syncManager.isAuthenticatingNextCloud)
            .labelsHidden()

            TextField(L10n.text("Server URL"), text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating || appState.syncManager.isAuthenticatingNextCloud)

            switch nextCloudMode {
            case .browser:
                if appState.syncManager.isAuthenticatingNextCloud {
                    HStack(spacing: 8) {
                        ProgressView().scaleEffect(0.7)
                        LText("Waiting for sign-in in browser…")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                } else {
                    LText("Click Re-authorize to open your server's login page in the browser. A new app password is issued and replaces the stored one.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
            case .appPassword:
                TextField(L10n.text("Username"), text: $username)
                    .textFieldStyle(.roundedBorder)
                    .textContentType(.username)
                    .disabled(isAuthenticating)
                SecureField(L10n.text("App Password"), text: $password)
                    .textFieldStyle(.roundedBorder)
                    .disabled(isAuthenticating)
                    .onSubmit { save() }
            }
        }
    }

    private var pCloudFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            SecureField(L10n.text("Access Token (pcauth cookie value)"), text: $apiToken)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            Divider().padding(.vertical, 4)
            LText("Or sign in with email and password (pCloud may refuse to issue a new token to third-party apps — paste a pcauth cookie above if that happens).")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
        }
    }

    private var kDriveFields: some View {
        VStack(spacing: 12) {
            SecureField(L10n.text("API Token"), text: $apiToken)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { save() }
            LText("Paste a new Infomaniak API token. The existing workspace selection is preserved.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }

    private var iCloudHint: some View {
        LText("iCloud uses your Mac's system sign-in — there's nothing to edit here. Manage iCloud Drive in System Settings → Apple Account → iCloud.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
    }

    private var teraboxHint: some View {
        LText("TeraBox signs in with a QR code scanned in the TeraBox app — there's nothing to edit here. To reconnect, remove this account and add it again.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
    }

    private var ftpFields: some View {
        VStack(spacing: 12) {
            HStack(spacing: 8) {
                TextField(L10n.text("Server"), text: $serverURL)
                    .textFieldStyle(.roundedBorder)
                    .disabled(isAuthenticating)
                TextField(L10n.text("Port"), text: $port)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 70)
                    .disabled(isAuthenticating)
            }
            TextField(L10n.text("Username"), text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)
            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { save() }
            TextField(L10n.text("Initial path"), text: $remotePath)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            Toggle(L10n.text("Use FTPS (TLS)"), isOn: $ftpUseTLS)
                .disabled(isAuthenticating)
            if ftpUseTLS {
                Toggle(L10n.text("Allow self-signed certificate"), isOn: $ftpAllowSelfSigned)
                    .disabled(isAuthenticating)
            }
        }
    }

    // MARK: - Buttons

    private var actionButtons: some View {
        HStack(spacing: 12) {
            Button(L10n.text("Cancel")) { dismiss() }
                .keyboardShortcut(.cancelAction)
                .disabled(isAuthenticating)

            Spacer()
            if isAuthenticating {
                ProgressView().scaleEffect(0.7)
            }

            primaryButton
        }
    }

    @ViewBuilder
    private var primaryButton: some View {
        switch account.providerType {
        case .iCloud, .terabox, .jottacloud, .googleDrivePicker:
            EmptyView()
        case .dropbox, .googleDrive, .oneDrive, .box:
            let isPendingBrowserAuth = appState.syncManager.isAuthenticatingGoogleDrive
                || appState.syncManager.isAuthenticatingDropbox
                || appState.syncManager.isAuthenticatingOneDrive
                || appState.syncManager.isAuthenticatingBox
            if isPendingBrowserAuth {
                Button(L10n.text("Cancel Sign-In"), role: .destructive) {
                    saveTask?.cancel()
                    saveTask = nil
                    appState.syncManager.cancelPendingBrowserAuth()
                    isAuthenticating = false
                }
            } else {
                Button(L10n.text("Re-authorize")) { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(isAuthenticating)
            }
        case .nextCloud where nextCloudMode == .browser:
            if appState.syncManager.isAuthenticatingNextCloud {
                Button(L10n.text("Cancel Sign-In"), role: .destructive) {
                    saveTask?.cancel()
                    saveTask = nil
                    appState.syncManager.cancelPendingBrowserAuth()
                    isAuthenticating = false
                }
            } else {
                Button(L10n.text("Re-authorize")) { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(serverURL.isEmpty || isAuthenticating)
            }
        default:
            Button(L10n.text("Save")) { save() }
                .keyboardShortcut(.defaultAction)
                .disabled(!hasChanges || !isFormValid || isAuthenticating)
        }
    }

    // MARK: - Diff check (drives Save enablement)

    /// True iff at least one field the user can edit differs from the
    /// initial snapshot. Save stays disabled until they actually change
    /// something — matches design decision (a) from issue follow-up.
    private var hasChanges: Bool {
        switch account.providerType {
        case .dropbox, .googleDrive, .oneDrive, .box, .iCloud, .terabox, .jottacloud, .googleDrivePicker:
            return false  // these use Re-authorize / no-op buttons
        case .nextCloud:
            switch nextCloudMode {
            case .browser:
                return serverURL != initial.serverURL || true
                // Browser flow always counts as a meaningful change (the
                // user explicitly clicked Re-authorize), so the button
                // doesn't gate on diff. The .disabled() above is what
                // ultimately controls Re-authorize availability.
            case .appPassword:
                return serverURL != initial.serverURL
                    || username != initial.username
                    || !password.isEmpty
            }
        case .koofr, .gmxCloud:
            return email != initial.email || !password.isEmpty
        case .mega, .filen:
            return email != initial.email
                || !password.isEmpty
                || (account.providerType == .mega ? !megaOTP.isEmpty : !filenTwoFactor.isEmpty)
        case .internxt:
            return email != initial.email
                || !password.isEmpty
                || !internxtTwoFactor.isEmpty
        case .seafile:
            return serverURL != initial.serverURL
                || email != initial.email
                || !password.isEmpty
                || seafileAllowSelfSigned != initial.allowSelfSignedCertificate
                || !seafileOTP.isEmpty
        case .webDAV:
            return serverURL != initial.serverURL
                || username != initial.username
                || !password.isEmpty
        case .ftp:
            return serverURL != initial.host
                || username != initial.username
                || !password.isEmpty
                || port != "\(initial.port)"
                || remotePath != (initial.remotePath.isEmpty ? "/" : initial.remotePath)
                || ftpUseTLS != initial.ftpUseTLS
                || ftpAllowSelfSigned != initial.allowSelfSignedCertificate
        case .sftp:
            return serverURL != initial.host
                || port != "\(initial.port)"
                || username != initial.username
                || remotePath != initial.remotePath
                || sftpAuthMethod.rawValue != (initial.sftpAuth == .privateKey ? "SSH Key" : "Password")
                || !password.isEmpty
                || !sftpPrivateKeyContents.isEmpty
                || !sftpPassphrase.isEmpty
        case .synologyDrive:
            return serverURL != initial.serverURL
                || username != initial.username
                || !password.isEmpty
                || synologyAllowSelfSigned != initial.allowSelfSignedCertificate
                || !synologyOTP.isEmpty
        case .wordpress:
            return serverURL != initial.serverURL
                || username != initial.username
                || !password.isEmpty
        case .s3:
            return s3AccessKeyId != initial.s3AccessKeyId
                || s3SecretAccessKey != initial.s3SecretAccessKey
                || s3Region != initial.s3Region
                || s3Path != (account.rootPath == "/" ? "" : String(account.rootPath.dropFirst()))
        case .s3Compatible:
            return s3AccessKeyId != initial.s3AccessKeyId
                || s3SecretAccessKey != initial.s3SecretAccessKey
                || s3Region != initial.s3Region
                || s3Endpoint != initial.s3Endpoint
                || s3DisplayName != initial.s3DisplayName
                || s3Path != (account.rootPath == "/" ? "" : String(account.rootPath.dropFirst()))
        case .synologyC2:
            return s3AccessKeyId != initial.s3AccessKeyId
                || s3SecretAccessKey != initial.s3SecretAccessKey
                || s3Region != initial.s3Region
        case .pCloud:
            return !apiToken.isEmpty || !email.isEmpty || !password.isEmpty
        case .kDrive:
            return !apiToken.isEmpty
        }
    }

    private var isFormValid: Bool {
        switch account.providerType {
        case .koofr, .gmxCloud, .mega:
            return !email.isEmpty && !password.isEmpty
        case .filen:
            return !email.isEmpty && !password.isEmpty
        case .internxt:
            return !email.isEmpty && !password.isEmpty
        case .seafile:
            return !serverURL.isEmpty && !email.isEmpty && !password.isEmpty
        case .webDAV:
            return !serverURL.isEmpty && !username.isEmpty && !password.isEmpty
        case .ftp:
            return !serverURL.isEmpty && !username.isEmpty && !password.isEmpty
        case .sftp:
            if serverURL.isEmpty || username.isEmpty { return false }
            switch sftpAuthMethod {
            case .password: return !password.isEmpty
            case .privateKey: return !sftpPrivateKeyContents.isEmpty
            }
        case .synologyDrive:
            return !serverURL.isEmpty && !username.isEmpty && !password.isEmpty
        case .wordpress:
            return !serverURL.isEmpty && !username.isEmpty && !password.isEmpty
        case .nextCloud:
            switch nextCloudMode {
            case .browser: return !serverURL.isEmpty
            case .appPassword: return !serverURL.isEmpty && !username.isEmpty && !password.isEmpty
            }
        case .s3:
            return !s3AccessKeyId.isEmpty && !s3SecretAccessKey.isEmpty && !s3Region.isEmpty
        case .s3Compatible:
            return !s3AccessKeyId.isEmpty && !s3SecretAccessKey.isEmpty && !s3Endpoint.isEmpty
        case .synologyC2:
            return !s3AccessKeyId.isEmpty && !s3SecretAccessKey.isEmpty && !s3Region.isEmpty
        case .pCloud:
            return !apiToken.isEmpty || (!email.isEmpty && !password.isEmpty)
        case .kDrive:
            return !apiToken.isEmpty
        case .dropbox, .googleDrive, .oneDrive, .box, .iCloud, .terabox, .jottacloud, .googleDrivePicker:
            return true
        }
    }

    // MARK: - Prefill

    private func primeFromSnapshot() {
        let snap = CloudAccountEditLoader.snapshot(for: account.id, providerType: account.providerType) ?? CloudAccountEditSnapshot()
        initial = snap
        email = snap.email
        username = snap.username
        serverURL = snap.serverURL
        if snap.port > 0 { port = "\(snap.port)" }
        if !snap.host.isEmpty { serverURL = snap.host }  // SFTP uses host
        remotePath = snap.remotePath.isEmpty ? "/" : snap.remotePath
        sftpAuthMethod = snap.sftpAuth == .privateKey ? .privateKey : .password
        s3AccessKeyId = snap.s3AccessKeyId
        s3SecretAccessKey = snap.s3SecretAccessKey
        s3Region = snap.s3Region.isEmpty ? s3Region : snap.s3Region
        s3Endpoint = snap.s3Endpoint
        s3DisplayName = snap.s3DisplayName
        // S3 path comes from CloudAccount.rootPath, not the credentials.
        s3Path = account.rootPath == "/" ? "" : String(account.rootPath.dropFirst())
        seafileAllowSelfSigned = snap.allowSelfSignedCertificate
        synologyAllowSelfSigned = snap.allowSelfSignedCertificate
        ftpUseTLS = snap.ftpUseTLS
        ftpAllowSelfSigned = snap.allowSelfSignedCertificate
        // For SFTP, prefill from rootPath as well in case the snapshot
        // didn't carry it.
        if account.providerType == .sftp, !account.rootPath.isEmpty, account.rootPath != "/" {
            remotePath = account.rootPath
        }
    }

    // MARK: - Save

    private func save() {
        guard !isAuthenticating else { return }
        isAuthenticating = true
        let id = account.id

        saveTask = Task {
            switch account.providerType {
            case .dropbox: await appState.syncManager.updateDropboxAccount(accountId: id)
            case .googleDrive: await appState.syncManager.updateGoogleDriveAccount(accountId: id)
            case .oneDrive: await appState.syncManager.updateOneDriveAccount(accountId: id)
            case .box: await appState.syncManager.updateBoxAccount(accountId: id)
            case .nextCloud:
                switch nextCloudMode {
                case .browser:
                    await appState.syncManager.updateNextCloudAccountViaBrowser(accountId: id, serverURL: serverURL)
                case .appPassword:
                    await appState.syncManager.updateNextCloudAccount(accountId: id, serverURL: serverURL, username: username, appPassword: password)
                }
            case .koofr:
                await appState.syncManager.updateKoofrAccount(accountId: id, email: email, appPassword: password)
            case .mega:
                let otp = megaOTP.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.updateMegaAccount(accountId: id, email: email, password: password, mfaCode: otp.isEmpty ? nil : otp)
            case .gmxCloud:
                await appState.syncManager.updateGMXCloudAccount(accountId: id, email: email, password: password)
            case .filen:
                let code = filenTwoFactor.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.updateFilenAccount(accountId: id, email: email, password: password, twoFactorCode: code)
            case .internxt:
                let code = internxtTwoFactor.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.updateInternxtAccount(accountId: id, email: email, password: password, twoFactorCode: code)
            case .seafile:
                let otp = seafileOTP.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.updateSeafileAccount(
                    accountId: id,
                    serverURL: serverURL.trimmingCharacters(in: .whitespacesAndNewlines),
                    username: email.trimmingCharacters(in: .whitespacesAndNewlines),
                    password: password,
                    otp: otp.isEmpty ? nil : otp,
                    allowSelfSignedCertificate: seafileAllowSelfSigned
                )
            case .webDAV:
                await appState.syncManager.updateWebDAVAccount(accountId: id, serverURL: serverURL, username: username, password: password)
            case .sftp:
                let parsedPort = Int(port) ?? 22
                let resolvedRemote = remotePath.trimmingCharacters(in: .whitespacesAndNewlines)
                switch sftpAuthMethod {
                case .password:
                    await appState.syncManager.updateSFTPAccount(
                        accountId: id,
                        host: serverURL,
                        port: parsedPort,
                        username: username,
                        password: password,
                        remotePath: resolvedRemote
                    )
                case .privateKey:
                    let pass = sftpPassphrase.isEmpty ? nil : sftpPassphrase
                    await appState.syncManager.updateSFTPAccount(
                        accountId: id,
                        host: serverURL,
                        port: parsedPort,
                        username: username,
                        privateKey: sftpPrivateKeyContents,
                        passphrase: pass,
                        remotePath: resolvedRemote
                    )
                }
            case .synologyDrive:
                let otp = synologyOTP.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.updateSynologyDriveAccount(
                    accountId: id,
                    serverURL: serverURL,
                    username: username,
                    password: password,
                    otp: otp.isEmpty ? nil : otp,
                    allowSelfSignedCertificate: synologyAllowSelfSigned
                )
            case .wordpress:
                await appState.syncManager.updateWordPressAccount(accountId: id, siteURL: serverURL, username: username, appPassword: password)
            case .s3:
                await appState.syncManager.updateS3Account(
                    accountId: id,
                    accessKeyId: s3AccessKeyId.trimmingCharacters(in: .whitespacesAndNewlines),
                    secretAccessKey: s3SecretAccessKey,
                    region: s3Region.trimmingCharacters(in: .whitespacesAndNewlines),
                    rootPath: s3Path
                )
            case .s3Compatible:
                let displayName = s3DisplayName.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.updateS3CompatibleAccount(
                    accountId: id,
                    accessKeyId: s3AccessKeyId.trimmingCharacters(in: .whitespacesAndNewlines),
                    secretAccessKey: s3SecretAccessKey,
                    endpoint: s3Endpoint.trimmingCharacters(in: .whitespacesAndNewlines),
                    region: s3Region.trimmingCharacters(in: .whitespacesAndNewlines),
                    displayName: displayName.isEmpty ? nil : displayName,
                    rootPath: s3Path
                )
            case .synologyC2:
                await appState.syncManager.updateSynologyC2Account(
                    accountId: id,
                    accessKeyId: s3AccessKeyId.trimmingCharacters(in: .whitespacesAndNewlines),
                    secretAccessKey: s3SecretAccessKey,
                    region: s3Region.trimmingCharacters(in: .whitespacesAndNewlines)
                )
            case .pCloud:
                let trimmedToken = apiToken.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmedToken.isEmpty {
                    await appState.syncManager.updatePCloudAccount(accountId: id, accessToken: trimmedToken)
                } else {
                    await appState.syncManager.updatePCloudAccount(accountId: id, email: email, password: password)
                }
            case .kDrive:
                await appState.syncManager.updateKDriveAccount(accountId: id, apiToken: apiToken.trimmingCharacters(in: .whitespacesAndNewlines))
            case .ftp:
                let trimmedRemote = remotePath.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.updateFTPAccount(
                    accountId: id,
                    host: serverURL.trimmingCharacters(in: .whitespacesAndNewlines),
                    port: Int(port) ?? 21,
                    username: username.trimmingCharacters(in: .whitespacesAndNewlines),
                    password: password,
                    remotePath: trimmedRemote.isEmpty ? "/" : trimmedRemote,
                    useTLS: ftpUseTLS,
                    allowInvalidCertificate: ftpAllowSelfSigned
                )
            case .iCloud, .terabox, .jottacloud, .googleDrivePicker:
                break
            }

            if appState.syncManager.authError == nil {
                dismiss()
            }
            isAuthenticating = false
            saveTask = nil
        }
    }
}

/// Lighter-touch sibling of `SheetWindowConfigurator` — just makes the
/// edit sheet user-resizable and pins a minimum size. Unlike the Add
/// configurator we do NOT call `setContentSize`: the Edit form is
/// per-provider and much smaller than the Add picker, so letting
/// SwiftUI size to content avoids the jump from natural → 620x720 the
/// user could see on first paint.
private struct EditWindowConfigurator: NSViewRepresentable {
    @Binding var didConfigure: Bool

    func makeNSView(context: Context) -> NSView { NSView() }

    func updateNSView(_ view: NSView, context: Context) {
        DispatchQueue.main.async {
            guard !didConfigure, let window = view.window else { return }
            didConfigure = true
            window.styleMask.insert(.resizable)
            window.minSize = NSSize(width: 460, height: 240)
        }
    }
}
