import SwiftUI
import FileFlussCore
import AppKit
import FileFlussCore

/// Adds `.resizable` to the underlying NSWindow's style mask (sheets on
/// macOS are non-resizable by default) and seeds an initial size derived
/// from the active screen, so the picker never opens taller than the
/// visible screen area on a 14" MacBook.
private struct SheetWindowConfigurator: NSViewRepresentable {
    @Binding var didConfigure: Bool

    func makeNSView(context: Context) -> NSView { NSView() }

    func updateNSView(_ view: NSView, context: Context) {
        DispatchQueue.main.async {
            guard !didConfigure, let window = view.window else { return }
            didConfigure = true

            window.styleMask.insert(.resizable)

            let visible = (window.screen ?? NSScreen.main)?.visibleFrame
                ?? CGRect(x: 0, y: 0, width: 1280, height: 800)
            // Leave ~80pt of breathing room around the sheet so it never
            // pins flush to the menu bar / dock on small displays.
            let maxW = max(460, visible.width - 80)
            let maxH = max(320, visible.height - 80)
            let targetW = min(640, maxW)
            let targetH = min(840, maxH)

            window.minSize = NSSize(width: 460, height: 320)
            window.setContentSize(NSSize(width: targetW, height: targetH))
            window.center()
        }
    }
}

struct AddCloudAccountView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    @State private var selectedProvider: CloudProviderType?
    @State private var email = ""
    @State private var password = ""
    @State private var apiToken = ""
    @State private var serverURL = ""
    @State private var username = ""
    @State private var port = "22"
    @State private var s3AccessKeyId = ""
    @State private var s3SecretAccessKey = ""
    @State private var s3Region = "us-east-1"
    @State private var s3Path = ""

    enum SFTPAuthMethod: String, CaseIterable, Hashable, Identifiable {
        case password = "Password"
        case privateKey = "SSH Key"
        var id: String { rawValue }
    }
    @State private var sftpAuthMethod: SFTPAuthMethod = .password
    @State private var sftpRemotePath = "/"
    @State private var sftpPassphrase = ""
    @State private var sftpPrivateKeyContents: String = ""
    @State private var sftpPrivateKeyFilename: String = ""
    @State private var sftpKeyImporterPresented = false
    @State private var sftpKeyImportError: String?

    // GoPro: either auto-discover (USB / Wi-Fi AP) or connect to a
    // home-network camera (COHN) with manually entered credentials.
    enum GoProAddMode: String, CaseIterable, Hashable, Identifiable {
        case scan
        case cohn
        var id: String { rawValue }
    }
    @State private var goProMode: GoProAddMode = .scan
    @State private var goProCameras: [GoProCamera] = []
    @State private var goProSelectedCameraID: String?
    @State private var goProScanning = false
    @State private var goProDidScan = false

    @State private var synologyOTP = ""
    @State private var synologyAllowSelfSigned = true

    @State private var seafileOTP = ""
    @State private var seafileAllowSelfSigned = false

    @State private var filenTwoFactor = ""
    @State private var internxtTwoFactor = ""
    @State private var ftpPort = "21"
    @State private var ftpRemotePath = "/"
    @State private var ftpUseTLS = false
    @State private var ftpAllowSelfSigned = false

    @State private var megaOTP = ""
    /// MEGA's login hits an anti-abuse proof-of-work (hashcash) that can
    /// take 10s–60s of SHA-256 churn before a Login response comes back.
    /// We surface a hint after 3s so the spinner doesn't look frozen.
    @State private var megaSlowLoginVisible = false
    @State private var megaSlowLoginTask: Task<Void, Never>?

    /// kDrive multi-drive picker state. After the user pastes a token and
    /// clicks Connect, we probe the token to enumerate the drives they have
    /// access to (organisation accounts have a personal drive + shared
    /// workspaces). If only one drive comes back we finalize immediately;
    /// otherwise we surface a picker so the user adds the specific workspace.
    @State private var kDriveDiscovery: KDriveProvider.Discovery?
    @State private var kDriveSelectedDriveId: Int?
    @State private var kDriveIsDiscovering = false

    enum NextCloudAuthMode: String, CaseIterable, Hashable, Identifiable {
        case browser = "Browser Login"
        case appPassword = "App Password"
        var id: String { rawValue }
    }
    @State private var nextCloudMode: NextCloudAuthMode = .browser

    @State private var synologyC2AccessKeyId = ""
    @State private var synologyC2SecretAccessKey = ""
    @State private var synologyC2Region = ""

    @State private var s3CompatibleAccessKeyId = ""
    @State private var s3CompatibleSecretAccessKey = ""
    @State private var s3CompatibleEndpoint = ""
    @State private var s3CompatibleRegion = ""
    @State private var s3CompatibleDisplayName = ""
    @State private var s3CompatiblePath = ""

    @State private var isAuthenticating = false
    @State private var didConfigureWindow = false
    @State private var loginTask: Task<Void, Never>?

    /// True iff the currently selected provider has an in-flight browser
    /// OAuth round-trip. Used to swap the bottom action button between
    /// Connect and Cancel Sign-In.
    private var isPendingBrowserAuth: Bool {
        switch selectedProvider {
        case .googleDrive: return appState.syncManager.isAuthenticatingGoogleDrive
        case .dropbox: return appState.syncManager.isAuthenticatingDropbox
        case .oneDrive: return appState.syncManager.isAuthenticatingOneDrive
        case .nextCloud: return appState.syncManager.isAuthenticatingNextCloud
        case .box: return appState.syncManager.isAuthenticatingBox
        default: return false
        }
    }

    private func cancelPendingBrowserAuth() {
        loginTask?.cancel()
        loginTask = nil
        appState.syncManager.cancelPendingBrowserAuth()
        isAuthenticating = false
    }

    /// Providers that show up under "Cloud Storage Providers". These are
    /// branded consumer/business cloud services. Sorted alphabetically
    /// by display name at render time.
    private let cloudStorageProviders: [CloudProviderType] = [
        .box, .dropbox, .filen, .gmxCloud, .googleDrive, .googleDrivePicker, .iCloud, .internxt,
        .jottacloud, .kDrive, .koofr, .mega, .nextCloud, .oneDrive, .pCloud, .seafile, .synologyC2, .terabox,
    ]

    /// Providers that show up under "Other Protocols" — generic transport
    /// or open-standard endpoints rather than a specific branded service.
    private let otherProtocolProviders: [CloudProviderType] = [
        .ftp, .s3, .s3Compatible, .sftp, .synologyDrive, .webDAV, .wordpress,
    ]

    /// Local devices reached over their own HTTP API rather than a cloud
    /// account — browsed and imported from, not synced to.
    private let deviceProviders: [CloudProviderType] = [
        .gopro,
    ]

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                if let selectedProvider {
                    loginForm(for: selectedProvider)
                } else {
                    providerPicker
                }
            }
            .padding(24)
            .frame(maxWidth: .infinity)
        }
        .frame(minWidth: 460, minHeight: 320)
        .background(SheetWindowConfigurator(didConfigure: $didConfigureWindow))
        .onDisappear {
            // Safety net for Esc / parent-window close: if the user
            // dismisses while a browser OAuth was pending, the flags
            // would otherwise stick for the rest of the session and
            // hide Connect the next time this sheet opens. See #24.
            loginTask?.cancel()
            loginTask = nil
            appState.syncManager.cancelPendingBrowserAuth()
        }
    }

    private var providerPicker: some View {
        VStack(spacing: 20) {
            LText("Add Cloud Account")
                .font(.title2.bold())

            LText("Select a cloud provider to connect:")
                .foregroundStyle(.secondary)

            providerSection(
                title: "Cloud Storage Providers",
                providers: cloudStorageProviders.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
            )

            providerSection(
                title: "Other Protocols",
                providers: otherProtocolProviders.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
            )

            providerSection(
                title: "Cameras & Devices",
                providers: deviceProviders.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
            )

            Button(L10n.text("Cancel")) { dismiss() }
                .keyboardShortcut(.cancelAction)
        }
    }

    private func providerSection(title: String, providers: [CloudProviderType]) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)

            LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 12)], spacing: 12) {
                ForEach(providers) { provider in
                    Button {
                        selectedProvider = provider
                    } label: {
                        VStack(spacing: 8) {
                            CloudProviderIcon(providerType: provider, size: 40)
                                .frame(height: 40)
                            Text(provider.displayName)
                                .font(.headline)
                                .lineLimit(1)
                                .minimumScaleFactor(0.7)
                        }
                        .frame(maxWidth: .infinity, minHeight: 80)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 12)
                        .background(.quaternary)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    @ViewBuilder
    private func loginForm(for provider: CloudProviderType) -> some View {
        VStack(spacing: 16) {
            HStack(spacing: 8) {
                CloudProviderIcon(providerType: provider, size: 24)
                Text(L10n.format("Sign in to %@", provider.displayName))
                    .font(.title2.bold())
            }

            switch provider {
            case .pCloud:
                pCloudFields
            case .kDrive:
                kDriveFields
            case .oneDrive:
                oneDriveFields
            case .googleDrive:
                googleDriveFields
            case .googleDrivePicker:
                googleDrivePickerFields
            case .dropbox:
                dropboxFields
            case .gmxCloud:
                gmxCloudFields
            case .nextCloud:
                nextCloudFields
            case .koofr:
                koofrFields
            case .mega:
                megaFields
            case .webDAV:
                webDAVFields
            case .sftp:
                sftpFields
            case .wordpress:
                wordPressFields
            case .s3:
                s3Fields
            case .synologyDrive:
                synologyDriveFields
            case .synologyC2:
                synologyC2Fields
            case .s3Compatible:
                s3CompatibleFields
            case .iCloud:
                iCloudFields
            case .box:
                boxFields
            case .seafile:
                seafileFields
            case .filen:
                filenFields
            case .internxt:
                internxtFields
            case .jottacloud:
                jottacloudFields
            case .terabox:
                teraboxFields
            case .ftp:
                ftpFields
            case .gopro:
                goProFields
            }

            if let authError = appState.syncManager.authError {
                Text(authError)
                    .foregroundStyle(.red)
                    .font(.caption)
                    .multilineTextAlignment(.center)
            }

            HStack(spacing: 12) {
                Button(L10n.text("Back")) {
                    selectedProvider = nil
                    appState.syncManager.authError = nil
                    email = ""
                    password = ""
                    apiToken = ""
                    serverURL = ""
                    username = ""
                    port = "22"
                    s3AccessKeyId = ""
                    s3SecretAccessKey = ""
                    s3Region = "us-east-1"
                    s3Path = ""
                    sftpAuthMethod = .password
                    sftpRemotePath = "/"
                    sftpPassphrase = ""
                    sftpPrivateKeyContents = ""
                    sftpPrivateKeyFilename = ""
                    sftpKeyImportError = nil
                    goProMode = .scan
                    goProCameras = []
                    goProSelectedCameraID = nil
                    goProScanning = false
                    goProDidScan = false
                    synologyOTP = ""
                    synologyAllowSelfSigned = true
                    seafileOTP = ""
                    seafileAllowSelfSigned = false
                    filenTwoFactor = ""
                    internxtTwoFactor = ""
                    megaOTP = ""
                    kDriveDiscovery = nil
                    kDriveSelectedDriveId = nil
                    kDriveIsDiscovering = false
                    synologyC2AccessKeyId = ""
                    synologyC2SecretAccessKey = ""
                    synologyC2Region = ""
                    s3CompatibleAccessKeyId = ""
                    s3CompatibleSecretAccessKey = ""
                    s3CompatibleEndpoint = ""
                    s3CompatibleRegion = ""
                    s3CompatibleDisplayName = ""
                    s3CompatiblePath = ""
                }
                .disabled(isAuthenticating)

                Spacer()

                if isAuthenticating {
                    ProgressView()
                        .scaleEffect(0.7)
                }

                if isPendingBrowserAuth {
                    Button(L10n.text("Cancel Sign-In"), role: .destructive) {
                        cancelPendingBrowserAuth()
                    }
                } else {
                    Button(L10n.text("Connect")) { login() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(isLoginDisabled)
                }
            }
        }
    }

    private var credentialFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var pCloudFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("pCloud no longer issues auth tokens via password login for third-party apps. You can still try email + password below, but most accounts will need to paste an access token.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            Divider().padding(.vertical, 4)

            LText("Or paste a pCloud access token")
                .font(.caption.weight(.semibold))

            VStack(alignment: .leading, spacing: 4) {
                LText("How to get your token:")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.secondary)
                LText("1. Sign in at my.pcloud.com in your browser")
                LText("2. Open DevTools (⌥⌘I in Chrome/Safari/Firefox)")
                LText("3. Chrome/Edge: Application tab → Storage → Cookies → https://my.pcloud.com")
                LText("   Firefox: Storage tab → Cookies → https://my.pcloud.com")
                LText("   Safari: Storage tab → Cookies → my.pcloud.com (enable Develop menu first)")
                LText("4. Copy the Value of the cookie named \"pcauth\" and paste it below")
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)

            Link(destination: URL(string: "https://my.pcloud.com")!) {
                HStack(spacing: 4) {
                    Image(systemName: "arrow.up.right.square")
                    LText("Open my.pcloud.com")
                }
                .font(.caption)
            }

            SecureField(L10n.text("Access Token (pcauth cookie value)"), text: $apiToken)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var kDriveFields: some View {
        VStack(spacing: 12) {
            LText("Create an API token in your Infomaniak account, then paste it below.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            // Free-account managers don't expose the token UI in the sidebar;
            // the direct URL works for every plan, so we surface it here.
            Button {
                if let url = URL(string: "https://manager.infomaniak.com/v3/ng/profile/user/token/list") {
                    NSWorkspace.shared.open(url)
                }
            } label: {
                Label(L10n.text("Open Infomaniak token page"), systemImage: "arrow.up.right.square")
            }
            .buttonStyle(.link)
            .font(.caption)

            SecureField(L10n.text("API Token"), text: $apiToken)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating || kDriveDiscovery != nil)
                .onSubmit { login() }

            if kDriveIsDiscovering {
                HStack(spacing: 8) {
                    ProgressView().scaleEffect(0.7)
                    LText("Looking up your drives…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            if let discovery = kDriveDiscovery, discovery.drives.count > 1 {
                VStack(alignment: .leading, spacing: 6) {
                    LText("Choose a drive to add")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Picker(L10n.text("Drive"), selection: $kDriveSelectedDriveId) {
                        ForEach(discovery.drives, id: \.id) { drive in
                            Text(drive.name).tag(drive.id as Int?)
                        }
                    }
                    .pickerStyle(.menu)
                    .labelsHidden()
                    LText("To use more than one drive, add the kDrive account again with the same token and pick a different drive.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var oneDriveFields: some View {
        VStack(spacing: 12) {
            if appState.syncManager.isAuthenticatingOneDrive {
                HStack(spacing: 8) {
                    ProgressView()
                        .scaleEffect(0.7)
                    LText("Waiting for sign-in in browser…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                LText("Click Connect to sign in with your Microsoft account. Your browser will open for authentication.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var googleDriveFields: some View {
        VStack(spacing: 12) {
            if appState.syncManager.isAuthenticatingGoogleDrive {
                HStack(spacing: 8) {
                    ProgressView()
                        .scaleEffect(0.7)
                    LText("Waiting for sign-in in browser…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                LText("Click Connect to sign in with your Google account. Your browser will open for authentication.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                LText("This full-access Google Drive option has reached its user limit. New users should use “Google Drive (Selected Folders)” instead.")
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var googleDrivePickerFields: some View {
        VStack(spacing: 12) {
            Label(L10n.text("For new Google Drive accounts — no user limit"), systemImage: "checkmark.seal.fill")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.green)

            LText("Sign in with Google and select the folders FileFluss may use. You can add and manage files in those folders. Files already in them aren't listed — this access mode only exposes what FileFluss adds or you pick.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }

    private var gmxCloudFields: some View {
        VStack(spacing: 12) {
            LText("Sign in with your GMX email and password. GMX Cloud is accessed over WebDAV; this may not work with GMX accounts that have two-factor authentication enabled.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField(L10n.text("GMX Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var dropboxFields: some View {
        VStack(spacing: 12) {
            if appState.syncManager.isAuthenticatingDropbox {
                HStack(spacing: 8) {
                    ProgressView()
                        .scaleEffect(0.7)
                    LText("Waiting for sign-in in browser…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                LText("Click Connect to sign in with your Dropbox account. Your browser will open for authentication.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var boxFields: some View {
        VStack(spacing: 12) {
            if appState.syncManager.isAuthenticatingBox {
                HStack(spacing: 8) {
                    ProgressView()
                        .scaleEffect(0.7)
                    LText("Waiting for sign-in in browser…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                LText("Click Connect to sign in with your Box account. Your browser will open for authentication. Uploads are capped at 50 MB per file in this version.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var seafileFields: some View {
        VStack(spacing: 12) {
            TextField(L10n.text("Server URL (e.g. https://seafile.example.com)"), text: $serverURL)
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
                .onSubmit { login() }
            TextField(L10n.text("Two-factor code (only if enabled)"), text: $seafileOTP)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            Toggle(L10n.text("Allow self-signed certificate"), isOn: $seafileAllowSelfSigned)
                .disabled(isAuthenticating)
                .help(L10n.text("Enable when the server presents a self-signed or LAN-only TLS certificate. Trust is scoped to the host you entered."))
            LText("Seafile doesn't support browser OAuth — the password is exchanged once for a long-lived API token, then discarded. Self-hosted servers work too.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }

    private var nextCloudFields: some View {
        VStack(spacing: 12) {
            Picker(L10n.text("Sign-in method"), selection: $nextCloudMode) {
                ForEach(NextCloudAuthMode.allCases) { mode in
                    Text(L10n.text(mode.rawValue)).tag(mode)
                }
            }
            .pickerStyle(.segmented)
            .disabled(isAuthenticating || appState.syncManager.isAuthenticatingNextCloud)
            .labelsHidden()

            TextField(L10n.text("Server URL (e.g. https://cloud.example.com)"), text: $serverURL)
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
                    LText("Click Connect to open your Nextcloud server's login page in the browser. Sign in there with your normal username and password (2FA supported); FileFluss receives an app-specific password back automatically.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
            case .appPassword:
                LText("Or enter your username and an app-specific password manually. Create one at Settings → Security → Devices & sessions.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)

                TextField(L10n.text("Username"), text: $username)
                    .textFieldStyle(.roundedBorder)
                    .textContentType(.username)
                    .disabled(isAuthenticating)

                SecureField(L10n.text("App Password"), text: $password)
                    .textFieldStyle(.roundedBorder)
                    .disabled(isAuthenticating)
                    .onSubmit { login() }
            }
        }
    }

    private var koofrFields: some View {
        VStack(spacing: 12) {
            LText("Create an app password at koofr.net → Preferences → Password, then enter your credentials below.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField(L10n.text("App Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var sftpFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("Enter your SFTP server details. You can authenticate with either a password or a private SSH key, and pin the panel to a specific remote folder on first open.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack(spacing: 8) {
                TextField(L10n.text("Hostname (e.g. server.example.com)"), text: $serverURL)
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

            TextField(L10n.text("Remote Path (e.g. /home/me or leave as /)"), text: $sftpRemotePath)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            Picker(L10n.text("Authentication"), selection: $sftpAuthMethod) {
                ForEach(SFTPAuthMethod.allCases) { method in
                    Text(L10n.text(method.rawValue)).tag(method)
                }
            }
            .pickerStyle(.segmented)
            .disabled(isAuthenticating)

            if sftpAuthMethod == .privateKey {
                sftpKeyFields
            } else {
                sftpPasswordField
            }
        }
    }

    private var sftpPasswordField: some View {
        SecureField(L10n.text("Password"), text: $password)
            .textFieldStyle(.roundedBorder)
            .textContentType(.password)
            .disabled(isAuthenticating)
            .onSubmit { login() }
    }

    private var sftpKeyFields: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Button {
                    sftpKeyImportError = nil
                    sftpKeyImporterPresented = true
                } label: {
                    Label(sftpPrivateKeyFilename.isEmpty ? "Choose Private Key…" : "Replace Private Key…",
                          systemImage: "key.horizontal")
                }
                .disabled(isAuthenticating)

                if !sftpPrivateKeyFilename.isEmpty {
                    Text(sftpPrivateKeyFilename)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    Button {
                        sftpPrivateKeyContents = ""
                        sftpPrivateKeyFilename = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .disabled(isAuthenticating)
                }
            }

            if let err = sftpKeyImportError {
                Text(err)
                    .font(.caption)
                    .foregroundStyle(.red)
            }

            SecureField(L10n.text("Passphrase (leave empty if key has none)"), text: $sftpPassphrase)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            LText("FileFluss reads the key file once and stores its contents in the macOS Keychain — moving or deleting the original file later won't affect this account.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
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
                        sftpKeyImportError = L10n.text("That doesn't look like a PEM private key. Expected file to start with -----BEGIN.")
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

    private var webDAVFields: some View {
        VStack(spacing: 12) {
            LText("Enter your WebDAV server URL, username, and password.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField(L10n.text("Server URL (e.g. https://dav.example.com/files)"), text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)

            TextField(L10n.text("Username"), text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
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
                .onSubmit { login() }
            TextField(L10n.text("Two-factor code (only if enabled)"), text: $filenTwoFactor)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            LText("Filen is end-to-end encrypted, so there's no browser OAuth — the password is used locally to derive your master key, then discarded. Only v2 accounts are supported in this release; new Filen accounts default to v2.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
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
                .onSubmit { login() }
            TextField(L10n.text("Two-factor code (only if enabled)"), text: $internxtTwoFactor)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            LText("Internxt is end-to-end encrypted — there's no browser OAuth. Your password is used locally to unlock your account mnemonic and derive per-file keys, then discarded.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }

    private var jottacloudFields: some View {
        VStack(spacing: 12) {
            LText("Generate a Personal Login Token in your Jottacloud account, then paste it below.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            Button {
                if let url = URL(string: "https://www.jottacloud.com/web/secure") {
                    NSWorkspace.shared.open(url)
                }
            } label: {
                Label(L10n.text("Open Jottacloud token page"), systemImage: "arrow.up.right.square")
            }
            .buttonStyle(.link)
            .font(.caption)

            SecureField(L10n.text("Personal Login Token"), text: $apiToken)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            LText("Jottacloud has no public API, so FileFluss uses the same interface as the official command-line tool. The token is exchanged for a login that's stored in your keychain.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }

    @ViewBuilder
    private var goProFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Picker("", selection: $goProMode) {
                Text(L10n.text("USB or Wi-Fi")).tag(GoProAddMode.scan)
                Text(L10n.text("Home Wi-Fi (COHN)")).tag(GoProAddMode.cohn)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            switch goProMode {
            case .scan: goProScanFields
            case .cohn: goProCOHNFields
            }
        }
    }

    private var goProScanFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("Turn the camera on, then either connect it by USB or join its Wi-Fi network. Scan to find it.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.leading)

            Button {
                scanForGoPro()
            } label: {
                HStack(spacing: 6) {
                    if goProScanning {
                        ProgressView().scaleEffect(0.6)
                    } else {
                        Image(systemName: "antenna.radiowaves.left.and.right")
                    }
                    Text(goProScanning ? L10n.text("Scanning…") : L10n.text("Scan for Cameras"))
                }
            }
            .disabled(goProScanning || isAuthenticating)

            ForEach(goProCameras) { camera in
                Button {
                    goProSelectedCameraID = camera.id
                } label: {
                    HStack(spacing: 10) {
                        Image(systemName: camera.mode == .wifiAP ? "wifi" : "cable.connector")
                            .foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(camera.name)
                            Text(camera.ipAddress)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        if goProSelectedCameraID == camera.id {
                            Image(systemName: "checkmark.circle.fill")
                                .foregroundStyle(.tint)
                        }
                    }
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        goProSelectedCameraID == camera.id
                            ? AnyShapeStyle(Color.accentColor.opacity(0.12))
                            : AnyShapeStyle(.quaternary),
                        in: RoundedRectangle(cornerRadius: 8)
                    )
                }
                .buttonStyle(.plain)
            }

            if goProDidScan && goProCameras.isEmpty && !goProScanning {
                LText("No camera found. Make sure it's on and connected by USB or Wi-Fi, then scan again.")
                    .font(.caption)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.leading)
            }
        }
    }

    private var goProCOHNFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("Set up \"Camera on Home Network\" in the GoPro Quik app, then enter the camera's IP address and the COHN username and password it shows.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.leading)

            TextField(L10n.text("Camera IP address"), text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            TextField(L10n.text("COHN Username"), text: $username)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            SecureField(L10n.text("COHN Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private func scanForGoPro() {
        goProScanning = true
        goProDidScan = false
        Task {
            let found = await appState.syncManager.scanForGoProCameras()
            goProCameras = found
            if goProSelectedCameraID == nil || !found.contains(where: { $0.id == goProSelectedCameraID }) {
                goProSelectedCameraID = found.first?.id
            }
            goProScanning = false
            goProDidScan = true
        }
    }

    private var teraboxFields: some View {
        VStack(spacing: 12) {
            if let qrData = appState.syncManager.teraboxQRImageData, let image = NSImage(data: qrData) {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.none)
                    .frame(width: 200, height: 200)
                LText("Open the TeraBox app and scan this QR code to authorize.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                ProgressView()
            } else {
                LText("TeraBox uses device-code sign-in: tap Connect to get a QR code, then scan it with the TeraBox app to authorize. Access is limited to this app's folder on your TeraBox drive.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var ftpFields: some View {
        VStack(spacing: 12) {
            HStack(spacing: 8) {
                TextField(L10n.text("Server (e.g. ftp.example.com)"), text: $serverURL)
                    .textFieldStyle(.roundedBorder)
                    .disabled(isAuthenticating)
                TextField(L10n.text("Port"), text: $ftpPort)
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
                .onSubmit { login() }
            TextField(L10n.text("Initial path (optional)"), text: $ftpRemotePath)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
            Toggle(L10n.text("Use FTPS (TLS)"), isOn: $ftpUseTLS)
                .disabled(isAuthenticating)
            if ftpUseTLS {
                Toggle(L10n.text("Allow self-signed certificate"), isOn: $ftpAllowSelfSigned)
                    .disabled(isAuthenticating)
            }
            LText("Plain FTP sends credentials and data unencrypted. Enable FTPS when the server supports it.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }

    private var megaFields: some View {
        VStack(spacing: 12) {
            LText("Sign in with your Mega email and password.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField(L10n.text("Email"), text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Password"), text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            TextField(L10n.text("Two-factor code (only if enabled)"), text: $megaOTP)
                .textFieldStyle(.roundedBorder)
                .textContentType(.oneTimeCode)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            if megaSlowLoginVisible {
                LText("Solving security challenge — may take a moment…")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.2), value: megaSlowLoginVisible)
    }

    private var iCloudFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("FileFluss uses your Mac's existing iCloud sign-in — no password needed here. iCloud Drive must be turned on in System Settings → Apple Account → iCloud → iCloud Drive.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            LText("Files that aren't currently downloaded show a small iCloud badge in the file list. Copying or syncing them triggers a transparent download first.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var s3CompatibleFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("Generic S3-compatible storage — Hetzner Object Storage, MinIO, Wasabi, Backblaze B2, Cloudflare R2, DigitalOcean Spaces, Linode, and the like. Paste the endpoint URL from your provider's console along with the access key it issued.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField(L10n.text("Access Key ID"), text: $s3CompatibleAccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Secret Access Key"), text: $s3CompatibleSecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField(L10n.text("Endpoint URL (e.g. https://fsn1.your-objectstorage.com)"), text: $s3CompatibleEndpoint)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField(L10n.text("Region (leave empty to auto-detect, e.g. fsn1, us-east-1, auto)"), text: $s3CompatibleRegion)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField(L10n.text("Display name (optional, e.g. \"Hetzner FSN1\")"), text: $s3CompatibleDisplayName)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField(L10n.text("Path (optional, e.g. my-bucket or my-bucket/my-folder)"), text: $s3CompatiblePath)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            LText("Leave blank to see all buckets. Otherwise the panel opens directly at this bucket or sub-folder.")
                .font(.caption2)
                .foregroundStyle(.tertiary)

            LText("Region tips: Hetzner uses the location code (fsn1, nbg1, hel1). Cloudflare R2 expects \"auto\". MinIO and most Backblaze B2 setups want \"us-east-1\".")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private var synologyC2Fields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("Synology C2 Object Storage is S3-compatible. Generate an access key in the C2 console (Storage Account → Access Keys), then paste it below along with the endpoint URL the console shows for your storage.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField(L10n.text("Access Key ID"), text: $synologyC2AccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Secret Access Key"), text: $synologyC2SecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField(L10n.text("Endpoint URL (e.g. https://eu-005.s3.synologyc2.net)"), text: $synologyC2Region)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            LText("Find the exact endpoint in your C2 console on the bucket detail page. Pasting the full URL is fine; FileFluss extracts the hostname and region automatically.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private var synologyDriveFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("Enter your NAS hostname (or QuickConnect URL) and DSM credentials. The default port is 5001 for HTTPS.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField(L10n.text("Server URL (e.g. https://my-nas.local:5001)"), text: $serverURL)
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
                .onSubmit { login() }

            TextField(L10n.text("OTP Code (only if 2-factor auth is on)"), text: $synologyOTP)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            Toggle(L10n.text("Allow self-signed certificate"), isOn: $synologyAllowSelfSigned)
                .help(L10n.text("Most home Synology NAS units use a self-signed certificate. Turn this off only if you've installed a real CA-signed cert (e.g. Let's Encrypt)."))
                .disabled(isAuthenticating)
        }
    }

    private var s3Fields: some View {
        VStack(alignment: .leading, spacing: 12) {
            LText("Enter an AWS access key with S3 permissions and the region your buckets live in. You can create a key under IAM → Users → Security credentials → Access keys.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField(L10n.text("Access Key ID (e.g. AKIA…)"), text: $s3AccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            SecureField(L10n.text("Secret Access Key"), text: $s3SecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            HStack {
                LText("Region")
                    .frame(width: 80, alignment: .leading)
                Picker(L10n.text(""), selection: $s3Region) {
                    ForEach(S3RegionList.allRegions, id: \.code) { region in
                        Text("\(region.displayName) (\(region.code))").tag(region.code)
                    }
                }
                .labelsHidden()
                .disabled(isAuthenticating)
            }

            LText("Tip: paste the exact region code (e.g. eu-central-1) from the AWS console URL if you don't see your region listed.")
                .font(.caption2)
                .foregroundStyle(.tertiary)

            TextField(L10n.text("Or type a custom region code"), text: $s3Region)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField(L10n.text("Path (optional, e.g. my-bucket or my-bucket/my-folder)"), text: $s3Path)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            LText("Leave blank to see all buckets. Otherwise the panel opens directly at this bucket or sub-folder.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private var wordPressFields: some View {
        VStack(spacing: 12) {
            LText("Enter your WordPress site URL and an Application Password. Create one in WordPress under Users → Profile → Application Passwords.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField(L10n.text("Site URL (e.g. https://example.com)"), text: $serverURL)
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
                .onSubmit { login() }
        }
    }

    private var isLoginDisabled: Bool {
        if isAuthenticating { return true }
        switch selectedProvider {
        case .pCloud: return apiToken.isEmpty && (email.isEmpty || password.isEmpty)
        case .kDrive: return apiToken.isEmpty
        case .oneDrive: return false
        case .googleDrive: return false
        case .googleDrivePicker: return false
        case .dropbox: return false
        case .nextCloud:
            switch nextCloudMode {
            case .browser: return serverURL.isEmpty
            case .appPassword: return serverURL.isEmpty || username.isEmpty || password.isEmpty
            }
        case .webDAV: return serverURL.isEmpty || username.isEmpty || password.isEmpty
        case .sftp:
            if serverURL.isEmpty || username.isEmpty { return true }
            switch sftpAuthMethod {
            case .password: return password.isEmpty
            case .privateKey: return sftpPrivateKeyContents.isEmpty
            }
        case .wordpress: return serverURL.isEmpty || username.isEmpty || password.isEmpty
        case .koofr: return email.isEmpty || password.isEmpty
        case .mega: return email.isEmpty || password.isEmpty
        case .gmxCloud: return email.isEmpty || password.isEmpty
        case .s3: return s3AccessKeyId.isEmpty || s3SecretAccessKey.isEmpty || s3Region.isEmpty
        case .synologyDrive: return serverURL.isEmpty || username.isEmpty || password.isEmpty
        case .synologyC2: return synologyC2AccessKeyId.isEmpty || synologyC2SecretAccessKey.isEmpty || synologyC2Region.isEmpty
        case .s3Compatible: return s3CompatibleAccessKeyId.isEmpty || s3CompatibleSecretAccessKey.isEmpty || s3CompatibleEndpoint.isEmpty
        case .iCloud: return false
        case .box: return false
        case .seafile: return serverURL.isEmpty || email.isEmpty || password.isEmpty
        case .filen: return email.isEmpty || password.isEmpty
        case .jottacloud: return apiToken.isEmpty
        case .terabox: return false  // device-code flow; the Connect button starts it
        case .ftp: return serverURL.isEmpty || username.isEmpty || password.isEmpty
        case .gopro:
            switch goProMode {
            case .scan: return goProSelectedCameraID == nil
            case .cohn: return serverURL.isEmpty || username.isEmpty || password.isEmpty
            }
        default: return email.isEmpty || password.isEmpty
        }
    }

    private func login() {
        guard !isAuthenticating else { return }
        isAuthenticating = true

        // MEGA only: show a "Solving security challenge…" hint after 3s
        // so the user knows the spinner isn't frozen during hashcash PoW.
        if selectedProvider == .mega {
            megaSlowLoginVisible = false
            megaSlowLoginTask?.cancel()
            megaSlowLoginTask = Task { @MainActor in
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                if !Task.isCancelled {
                    megaSlowLoginVisible = true
                }
            }
        }

        loginTask = Task {
            switch selectedProvider {
            case .kDrive:
                let token = apiToken.trimmingCharacters(in: .whitespacesAndNewlines)
                if let discovery = kDriveDiscovery {
                    // Second click — finalize with the chosen drive.
                    let chosenId = kDriveSelectedDriveId ?? discovery.drives.first?.id
                    let chosenName = discovery.drives.first(where: { $0.id == chosenId })?.name
                    await appState.syncManager.addKDriveAccount(
                        apiToken: token,
                        driveId: chosenId,
                        driveName: chosenName
                    )
                } else {
                    // First click — discover drives and decide whether to prompt.
                    do {
                        kDriveIsDiscovering = true
                        let discovery = try await appState.syncManager.discoverKDriveDrives(apiToken: token)
                        kDriveIsDiscovering = false
                        if discovery.drives.count <= 1 {
                            // One drive (or none) — finalize directly, no extra click.
                            let only = discovery.drives.first
                            await appState.syncManager.addKDriveAccount(
                                apiToken: token,
                                driveId: only?.id,
                                driveName: only?.name
                            )
                        } else {
                            kDriveDiscovery = discovery
                            kDriveSelectedDriveId = discovery.drives.first?.id
                            // Keep the sheet open so the user can pick.
                            appState.syncManager.authError = nil
                            isAuthenticating = false
                            return
                        }
                    } catch {
                        kDriveIsDiscovering = false
                        appState.syncManager.authError = error.localizedDescription
                    }
                }
            case .oneDrive:
                await appState.syncManager.addOneDriveAccount()
            case .googleDrive:
                await appState.syncManager.addGoogleDriveAccount()
            case .dropbox:
                await appState.syncManager.addDropboxAccount()
            case .gmxCloud:
                await appState.syncManager.addGMXCloudAccount(email: email, password: password)
            case .nextCloud:
                switch nextCloudMode {
                case .browser:
                    await appState.syncManager.addNextCloudAccountViaBrowser(serverURL: serverURL)
                case .appPassword:
                    await appState.syncManager.addNextCloudAccount(serverURL: serverURL, username: username, appPassword: password)
                }
            case .koofr:
                await appState.syncManager.addKoofrAccount(email: email, appPassword: password)
            case .mega:
                let otp = megaOTP.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addMegaAccount(
                    email: email,
                    password: password,
                    mfaCode: otp.isEmpty ? nil : otp
                )
            case .webDAV:
                await appState.syncManager.addWebDAVAccount(serverURL: serverURL, username: username, password: password)
            case .sftp:
                let trimmedRemote = sftpRemotePath.trimmingCharacters(in: .whitespacesAndNewlines)
                let resolvedRemote = trimmedRemote.isEmpty ? "/" : trimmedRemote
                let parsedPort = Int(port) ?? 22
                switch sftpAuthMethod {
                case .password:
                    await appState.syncManager.addSFTPAccount(
                        host: serverURL,
                        port: parsedPort,
                        username: username,
                        password: password,
                        remotePath: resolvedRemote
                    )
                case .privateKey:
                    let pass = sftpPassphrase.isEmpty ? nil : sftpPassphrase
                    await appState.syncManager.addSFTPAccount(
                        host: serverURL,
                        port: parsedPort,
                        username: username,
                        privateKey: sftpPrivateKeyContents,
                        passphrase: pass,
                        remotePath: resolvedRemote
                    )
                }
            case .ftp:
                let trimmedRemote = ftpRemotePath.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addFTPAccount(
                    host: serverURL.trimmingCharacters(in: .whitespacesAndNewlines),
                    port: Int(ftpPort) ?? 21,
                    username: username.trimmingCharacters(in: .whitespacesAndNewlines),
                    password: password,
                    remotePath: trimmedRemote.isEmpty ? "/" : trimmedRemote,
                    useTLS: ftpUseTLS,
                    allowInvalidCertificate: ftpAllowSelfSigned
                )
            case .wordpress:
                await appState.syncManager.addWordPressAccount(siteURL: serverURL, username: username, appPassword: password)
            case .s3:
                let trimmedRegion = s3Region.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addS3Account(
                    accessKeyId: s3AccessKeyId.trimmingCharacters(in: .whitespacesAndNewlines),
                    secretAccessKey: s3SecretAccessKey,
                    region: trimmedRegion.isEmpty ? "us-east-1" : trimmedRegion,
                    rootPath: s3Path
                )
            case .synologyDrive:
                let otp = synologyOTP.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addSynologyDriveAccount(
                    serverURL: serverURL,
                    username: username,
                    password: password,
                    otp: otp.isEmpty ? nil : otp,
                    allowSelfSignedCertificate: synologyAllowSelfSigned
                )
            case .synologyC2:
                let trimmedEndpoint = synologyC2Region.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addSynologyC2Account(
                    accessKeyId: synologyC2AccessKeyId.trimmingCharacters(in: .whitespacesAndNewlines),
                    secretAccessKey: synologyC2SecretAccessKey,
                    region: trimmedEndpoint
                )
            case .s3Compatible:
                let displayName = s3CompatibleDisplayName.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addS3CompatibleAccount(
                    accessKeyId: s3CompatibleAccessKeyId.trimmingCharacters(in: .whitespacesAndNewlines),
                    secretAccessKey: s3CompatibleSecretAccessKey,
                    endpoint: s3CompatibleEndpoint.trimmingCharacters(in: .whitespacesAndNewlines),
                    region: s3CompatibleRegion.trimmingCharacters(in: .whitespacesAndNewlines),
                    displayName: displayName.isEmpty ? nil : displayName,
                    rootPath: s3CompatiblePath
                )
            case .iCloud:
                await appState.syncManager.addICloudAccount()
            case .box:
                await appState.syncManager.addBoxAccount()
            case .seafile:
                let otp = seafileOTP.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addSeafileAccount(
                    serverURL: serverURL.trimmingCharacters(in: .whitespacesAndNewlines),
                    username: email.trimmingCharacters(in: .whitespacesAndNewlines),
                    password: password,
                    otp: otp.isEmpty ? nil : otp,
                    allowSelfSignedCertificate: seafileAllowSelfSigned
                )
            case .filen:
                let code = filenTwoFactor.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addFilenAccount(
                    email: email.trimmingCharacters(in: .whitespacesAndNewlines),
                    password: password,
                    twoFactorCode: code
                )
            case .internxt:
                let code = internxtTwoFactor.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addInternxtAccount(
                    email: email.trimmingCharacters(in: .whitespacesAndNewlines),
                    password: password,
                    twoFactorCode: code
                )
            case .terabox:
                await appState.syncManager.connectTeraBox()
            case .jottacloud:
                await appState.syncManager.addJottacloudAccount(
                    personalToken: apiToken.trimmingCharacters(in: .whitespacesAndNewlines)
                )
            case .googleDrivePicker:
                // Step 1: OAuth (system browser). Step 2: the Google Picker,
                // also in the system browser (it needs the user's signed-in
                // Google session), which hands back the chosen folders.
                do {
                    let creds = try await GoogleDrivePickerProvider.startOAuth()
                    let roots = try await GoogleDrivePickerServer().pickFolders(accessToken: creds.accessToken)
                    guard !roots.isEmpty else {
                        // Cancelled in the browser — stay on the form.
                        isAuthenticating = false
                        loginTask = nil
                        return
                    }
                    await appState.syncManager.addGoogleDrivePickerAccount(credentials: creds, roots: roots)
                } catch {
                    appState.syncManager.authError = error.localizedDescription
                }
            case .pCloud:
                let trimmedToken = apiToken.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmedToken.isEmpty {
                    await appState.syncManager.addPCloudAccount(accessToken: trimmedToken)
                } else {
                    await appState.syncManager.addPCloudAccount(email: email, password: password)
                }
            case .gopro:
                switch goProMode {
                case .scan:
                    if let camera = goProCameras.first(where: { $0.id == goProSelectedCameraID }) {
                        await appState.syncManager.addGoProAccount(camera: camera)
                    }
                case .cohn:
                    await appState.syncManager.addGoProCOHNAccount(
                        ipAddress: serverURL.trimmingCharacters(in: .whitespacesAndNewlines),
                        username: username.trimmingCharacters(in: .whitespacesAndNewlines),
                        password: password
                    )
                }
            default:
                break
            }
            megaSlowLoginTask?.cancel()
            megaSlowLoginTask = nil
            megaSlowLoginVisible = false
            if appState.syncManager.authError == nil {
                dismiss()
            }
            isAuthenticating = false
            loginTask = nil
        }
    }
}
