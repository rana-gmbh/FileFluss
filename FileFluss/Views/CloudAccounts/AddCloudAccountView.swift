import SwiftUI

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

    @State private var synologyOTP = ""
    @State private var synologyAllowSelfSigned = true

    @State private var synologyC2AccessKeyId = ""
    @State private var synologyC2SecretAccessKey = ""
    @State private var synologyC2Region = ""

    @State private var s3CompatibleAccessKeyId = ""
    @State private var s3CompatibleSecretAccessKey = ""
    @State private var s3CompatibleEndpoint = ""
    @State private var s3CompatibleRegion = ""
    @State private var s3CompatibleDisplayName = ""

    @State private var isAuthenticating = false

    // Only show providers that are implemented
    private let availableProviders: [CloudProviderType] = [.pCloud, .kDrive, .oneDrive, .googleDrive, .nextCloud, .koofr, .dropbox, .gmxCloud, .mega, .webDAV, .sftp, .wordpress, .s3, .synologyDrive, .synologyC2, .s3Compatible]

    var body: some View {
        VStack(spacing: 20) {
            if let selectedProvider {
                loginForm(for: selectedProvider)
            } else {
                providerPicker
            }
        }
        .padding(24)
        .frame(width: 560)
    }

    private var providerPicker: some View {
        VStack(spacing: 20) {
            Text("Add Cloud Account")
                .font(.title2.bold())

            Text("Select a cloud provider to connect:")
                .foregroundStyle(.secondary)

            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 12), count: 4), spacing: 12) {
                ForEach(availableProviders) { provider in
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

            Button("Cancel") { dismiss() }
                .keyboardShortcut(.cancelAction)
        }
    }

    @ViewBuilder
    private func loginForm(for provider: CloudProviderType) -> some View {
        VStack(spacing: 16) {
            HStack(spacing: 8) {
                CloudProviderIcon(providerType: provider, size: 24)
                Text("Sign in to \(provider.displayName)")
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
            default:
                credentialFields
            }

            if let authError = appState.syncManager.authError {
                Text(authError)
                    .foregroundStyle(.red)
                    .font(.caption)
                    .multilineTextAlignment(.center)
            }

            HStack(spacing: 12) {
                Button("Back") {
                    selectedProvider = nil
                    appState.syncManager.authError = nil
                    appState.syncManager.oneDriveDeviceCode = nil
                    appState.syncManager.isPollingForOneDrive = false
                    email = ""
                    password = ""
                    apiToken = ""
                    serverURL = ""
                    username = ""
                    port = "22"
                    s3AccessKeyId = ""
                    s3SecretAccessKey = ""
                    s3Region = "us-east-1"
                    sftpAuthMethod = .password
                    sftpRemotePath = "/"
                    sftpPassphrase = ""
                    sftpPrivateKeyContents = ""
                    sftpPrivateKeyFilename = ""
                    sftpKeyImportError = nil
                    synologyOTP = ""
                    synologyAllowSelfSigned = true
                    synologyC2AccessKeyId = ""
                    synologyC2SecretAccessKey = ""
                    synologyC2Region = ""
                    s3CompatibleAccessKeyId = ""
                    s3CompatibleSecretAccessKey = ""
                    s3CompatibleEndpoint = ""
                    s3CompatibleRegion = ""
                    s3CompatibleDisplayName = ""
                }
                .disabled(isAuthenticating)

                Spacer()

                if isAuthenticating {
                    ProgressView()
                        .scaleEffect(0.7)
                }

                if !appState.syncManager.isAuthenticatingGoogleDrive && !appState.syncManager.isAuthenticatingDropbox && (provider != .oneDrive || appState.syncManager.oneDriveDeviceCode == nil) {
                    Button("Connect") { login() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(isLoginDisabled)
                }
            }
        }
    }

    private var credentialFields: some View {
        VStack(spacing: 12) {
            TextField("Email", text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField("Password", text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var pCloudFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("pCloud no longer issues auth tokens via password login for third-party apps. You can still try email + password below, but most accounts will need to paste an access token.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("Email", text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField("Password", text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            Divider().padding(.vertical, 4)

            Text("Or paste a pCloud access token")
                .font(.caption.weight(.semibold))

            VStack(alignment: .leading, spacing: 4) {
                Text("How to get your token:")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.secondary)
                Text("1. Sign in at my.pcloud.com in your browser")
                Text("2. Open DevTools (⌥⌘I in Chrome/Safari/Firefox)")
                Text("3. Chrome/Edge: Application tab → Storage → Cookies → https://my.pcloud.com")
                Text("   Firefox: Storage tab → Cookies → https://my.pcloud.com")
                Text("   Safari: Storage tab → Cookies → my.pcloud.com (enable Develop menu first)")
                Text("4. Copy the Value of the cookie named \"pcauth\" and paste it below")
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)

            Link(destination: URL(string: "https://my.pcloud.com")!) {
                HStack(spacing: 4) {
                    Image(systemName: "arrow.up.right.square")
                    Text("Open my.pcloud.com")
                }
                .font(.caption)
            }

            SecureField("Access Token (pcauth cookie value)", text: $apiToken)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var kDriveFields: some View {
        VStack(spacing: 12) {
            Text("Create an API token at manager.infomaniak.com with kDrive access, then paste it below.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            SecureField("API Token", text: $apiToken)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var oneDriveFields: some View {
        VStack(spacing: 12) {
            if let deviceCode = appState.syncManager.oneDriveDeviceCode {
                // Show the device code for the user to enter at Microsoft's page
                Text("Enter the code below at Microsoft's sign-in page:")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)

                Text(deviceCode.userCode)
                    .font(.system(.title, design: .monospaced).bold())
                    .padding(.vertical, 8)
                    .padding(.horizontal, 16)
                    .background(.quaternary)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .textSelection(.enabled)

                Link(destination: URL(string: deviceCode.verificationUri)!) {
                    HStack(spacing: 4) {
                        Image(systemName: "arrow.up.right.square")
                        Text("Open Microsoft Login")
                    }
                }
                .buttonStyle(.borderedProminent)

                if appState.syncManager.isPollingForOneDrive {
                    HStack(spacing: 8) {
                        ProgressView()
                            .scaleEffect(0.7)
                        Text("Waiting for sign-in…")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            } else {
                Text("Click Connect to sign in with your Microsoft account. A code will appear for you to enter at Microsoft's login page.")
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
                    Text("Waiting for sign-in in browser…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                Text("Click Connect to sign in with your Google account. Your browser will open for authentication.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var gmxCloudFields: some View {
        VStack(spacing: 12) {
            Text("Sign in with your GMX email and password. GMX Cloud is accessed over WebDAV; this may not work with GMX accounts that have two-factor authentication enabled.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField("GMX Email", text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField("Password", text: $password)
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
                    Text("Waiting for sign-in in browser…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                Text("Click Connect to sign in with your Dropbox account. Your browser will open for authentication.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var nextCloudFields: some View {
        VStack(spacing: 12) {
            Text("Enter your Nextcloud server URL and an app password. Create one at Settings → Security → Devices & sessions.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField("Server URL (e.g. https://cloud.example.com)", text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)

            TextField("Username", text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)

            SecureField("App Password", text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var koofrFields: some View {
        VStack(spacing: 12) {
            Text("Create an app password at koofr.net → Preferences → Password, then enter your credentials below.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField("Email", text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField("App Password", text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var sftpFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Enter your SFTP server details. You can authenticate with either a password or a private SSH key, and pin the panel to a specific remote folder on first open.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack(spacing: 8) {
                TextField("Hostname (e.g. server.example.com)", text: $serverURL)
                    .textFieldStyle(.roundedBorder)
                    .disabled(isAuthenticating)

                TextField("Port", text: $port)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 60)
                    .disabled(isAuthenticating)
            }

            TextField("Username", text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)

            TextField("Remote Path (e.g. /home/me or leave as /)", text: $sftpRemotePath)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            Picker("Authentication", selection: $sftpAuthMethod) {
                ForEach(SFTPAuthMethod.allCases) { method in
                    Text(method.rawValue).tag(method)
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
        SecureField("Password", text: $password)
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

            SecureField("Passphrase (leave empty if key has none)", text: $sftpPassphrase)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            Text("FileFluss reads the key file once and stores its contents in the macOS Keychain — moving or deleting the original file later won't affect this account.")
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
                        sftpKeyImportError = "Key file is not a UTF-8 text PEM."
                        return
                    }
                    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard trimmed.hasPrefix("-----BEGIN") else {
                        sftpKeyImportError = "That doesn't look like a PEM private key. Expected file to start with -----BEGIN."
                        return
                    }
                    sftpPrivateKeyContents = text
                    sftpPrivateKeyFilename = url.lastPathComponent
                    sftpKeyImportError = nil
                } catch {
                    sftpKeyImportError = "Could not read key file: \(error.localizedDescription)"
                }
            case .failure(let error):
                sftpKeyImportError = error.localizedDescription
            }
        }
    }

    private var webDAVFields: some View {
        VStack(spacing: 12) {
            Text("Enter your WebDAV server URL, username, and password.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField("Server URL (e.g. https://dav.example.com/files)", text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)

            TextField("Username", text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)

            SecureField("Password", text: $password)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var megaFields: some View {
        VStack(spacing: 12) {
            Text("Sign in with your Mega email and password.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField("Email", text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .disabled(isAuthenticating)

            SecureField("Password", text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var s3CompatibleFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Generic S3-compatible storage — Hetzner Object Storage, MinIO, Wasabi, Backblaze B2, Cloudflare R2, DigitalOcean Spaces, Linode, and the like. Paste the endpoint URL from your provider's console along with the access key it issued.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("Access Key ID", text: $s3CompatibleAccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            SecureField("Secret Access Key", text: $s3CompatibleSecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField("Endpoint URL (e.g. https://fsn1.your-objectstorage.com)", text: $s3CompatibleEndpoint)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField("Region (leave empty to auto-detect, e.g. fsn1, us-east-1, auto)", text: $s3CompatibleRegion)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField("Display name (optional, e.g. \"Hetzner FSN1\")", text: $s3CompatibleDisplayName)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            Text("Region tips: Hetzner uses the location code (fsn1, nbg1, hel1). Cloudflare R2 expects \"auto\". MinIO and most Backblaze B2 setups want \"us-east-1\".")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private var synologyC2Fields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Synology C2 Object Storage is S3-compatible. Generate an access key in the C2 console (Storage Account → Access Keys), then paste it below along with the endpoint URL the console shows for your storage.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("Access Key ID", text: $synologyC2AccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            SecureField("Secret Access Key", text: $synologyC2SecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            TextField("Endpoint URL (e.g. https://eu-005.s3.synologyc2.net)", text: $synologyC2Region)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            Text("Find the exact endpoint in your C2 console on the bucket detail page. Pasting the full URL is fine; FileFluss extracts the hostname and region automatically.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private var synologyDriveFields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Enter your NAS hostname (or QuickConnect URL) and DSM credentials. The default port is 5001 for HTTPS.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("Server URL (e.g. https://my-nas.local:5001)", text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)

            TextField("Username", text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)

            SecureField("Password", text: $password)
                .textFieldStyle(.roundedBorder)
                .textContentType(.password)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            TextField("OTP Code (only if 2-factor auth is on)", text: $synologyOTP)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }

            Toggle("Allow self-signed certificate", isOn: $synologyAllowSelfSigned)
                .help("Most home Synology NAS units use a self-signed certificate. Turn this off only if you've installed a real CA-signed cert (e.g. Let's Encrypt).")
                .disabled(isAuthenticating)
        }
    }

    private var s3Fields: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Enter an AWS access key with S3 permissions and the region your buckets live in. You can create a key under IAM → Users → Security credentials → Access keys.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("Access Key ID (e.g. AKIA…)", text: $s3AccessKeyId)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            SecureField("Secret Access Key", text: $s3SecretAccessKey)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)

            HStack {
                Text("Region")
                    .frame(width: 80, alignment: .leading)
                Picker("", selection: $s3Region) {
                    ForEach(S3RegionList.allRegions, id: \.code) { region in
                        Text("\(region.displayName) (\(region.code))").tag(region.code)
                    }
                }
                .labelsHidden()
                .disabled(isAuthenticating)
            }

            Text("Tip: paste the exact region code (e.g. eu-central-1) from the AWS console URL if you don't see your region listed.")
                .font(.caption2)
                .foregroundStyle(.tertiary)

            TextField("Or type a custom region code", text: $s3Region)
                .textFieldStyle(.roundedBorder)
                .disabled(isAuthenticating)
                .onSubmit { login() }
        }
    }

    private var wordPressFields: some View {
        VStack(spacing: 12) {
            Text("Enter your WordPress site URL and an Application Password. Create one in WordPress under Users → Profile → Application Passwords.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            TextField("Site URL (e.g. https://example.com)", text: $serverURL)
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .disabled(isAuthenticating)

            TextField("Username", text: $username)
                .textFieldStyle(.roundedBorder)
                .textContentType(.username)
                .disabled(isAuthenticating)

            SecureField("Application Password", text: $password)
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
        case .dropbox: return false
        case .nextCloud: return serverURL.isEmpty || username.isEmpty || password.isEmpty
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
        default: return email.isEmpty || password.isEmpty
        }
    }

    private func login() {
        guard !isAuthenticating else { return }
        isAuthenticating = true
        Task {
            switch selectedProvider {
            case .kDrive:
                await appState.syncManager.addKDriveAccount(apiToken: apiToken)
            case .oneDrive:
                await appState.syncManager.addOneDriveAccount()
            case .googleDrive:
                await appState.syncManager.addGoogleDriveAccount()
            case .dropbox:
                await appState.syncManager.addDropboxAccount()
            case .gmxCloud:
                await appState.syncManager.addGMXCloudAccount(email: email, password: password)
            case .nextCloud:
                await appState.syncManager.addNextCloudAccount(serverURL: serverURL, username: username, appPassword: password)
            case .koofr:
                await appState.syncManager.addKoofrAccount(email: email, appPassword: password)
            case .mega:
                await appState.syncManager.addMegaAccount(email: email, password: password)
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
            case .wordpress:
                await appState.syncManager.addWordPressAccount(siteURL: serverURL, username: username, appPassword: password)
            case .s3:
                let trimmedRegion = s3Region.trimmingCharacters(in: .whitespacesAndNewlines)
                await appState.syncManager.addS3Account(
                    accessKeyId: s3AccessKeyId.trimmingCharacters(in: .whitespacesAndNewlines),
                    secretAccessKey: s3SecretAccessKey,
                    region: trimmedRegion.isEmpty ? "us-east-1" : trimmedRegion
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
                    displayName: displayName.isEmpty ? nil : displayName
                )
            case .pCloud:
                let trimmedToken = apiToken.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmedToken.isEmpty {
                    await appState.syncManager.addPCloudAccount(accessToken: trimmedToken)
                } else {
                    await appState.syncManager.addPCloudAccount(email: email, password: password)
                }
            default:
                break
            }
            if appState.syncManager.authError == nil && !appState.syncManager.isPollingForOneDrive {
                dismiss()
            }
            isAuthenticating = false
        }
    }
}
