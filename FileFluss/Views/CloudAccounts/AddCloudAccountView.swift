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
    @State private var isAuthenticating = false

    // Only show providers that are implemented
    private let availableProviders: [CloudProviderType] = [.pCloud, .kDrive, .oneDrive, .googleDrive, .nextCloud, .koofr, .dropbox, .mega, .webDAV]

    var body: some View {
        VStack(spacing: 20) {
            if let selectedProvider {
                loginForm(for: selectedProvider)
            } else {
                providerPicker
            }
        }
        .padding(24)
        .frame(width: 380)
    }

    private var providerPicker: some View {
        VStack(spacing: 20) {
            Text("Add Cloud Account")
                .font(.title2.bold())

            Text("Select a cloud provider to connect:")
                .foregroundStyle(.secondary)

            LazyVGrid(columns: [
                GridItem(.flexible()),
                GridItem(.flexible()),
            ], spacing: 16) {
                ForEach(availableProviders) { provider in
                    Button {
                        selectedProvider = provider
                    } label: {
                        VStack(spacing: 8) {
                            CloudProviderIcon(providerType: provider, size: 40)
                                .frame(height: 40)
                            Text(provider.displayName)
                                .font(.headline)
                        }
                        .frame(maxWidth: .infinity)
                        .padding()
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
            case .kDrive:
                kDriveFields
            case .oneDrive:
                oneDriveFields
            case .googleDrive:
                googleDriveFields
            case .dropbox:
                dropboxFields
            case .nextCloud:
                nextCloudFields
            case .koofr:
                koofrFields
            case .mega:
                megaFields
            case .webDAV:
                webDAVFields
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

    private var isLoginDisabled: Bool {
        if isAuthenticating { return true }
        switch selectedProvider {
        case .kDrive: return apiToken.isEmpty
        case .oneDrive: return false
        case .googleDrive: return false
        case .dropbox: return false
        case .nextCloud: return serverURL.isEmpty || username.isEmpty || password.isEmpty
        case .webDAV: return serverURL.isEmpty || username.isEmpty || password.isEmpty
        case .koofr: return email.isEmpty || password.isEmpty
        case .mega: return email.isEmpty || password.isEmpty
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
            case .nextCloud:
                await appState.syncManager.addNextCloudAccount(serverURL: serverURL, username: username, appPassword: password)
            case .koofr:
                await appState.syncManager.addKoofrAccount(email: email, appPassword: password)
            case .mega:
                await appState.syncManager.addMegaAccount(email: email, password: password)
            case .webDAV:
                await appState.syncManager.addWebDAVAccount(serverURL: serverURL, username: username, password: password)
            default:
                await appState.syncManager.addPCloudAccount(email: email, password: password)
            }
            if appState.syncManager.authError == nil && !appState.syncManager.isPollingForOneDrive {
                dismiss()
            }
            isAuthenticating = false
        }
    }
}
