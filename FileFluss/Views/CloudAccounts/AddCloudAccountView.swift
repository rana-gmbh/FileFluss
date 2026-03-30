import SwiftUI

struct AddCloudAccountView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    @State private var selectedProvider: CloudProviderType?
    @State private var email = ""
    @State private var password = ""
    @State private var apiToken = ""
    @State private var isAuthenticating = false

    // Only show providers that are implemented
    private let availableProviders: [CloudProviderType] = [.pCloud, .kDrive, .oneDrive]

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
                            Image(systemName: provider.icon)
                                .font(.largeTitle)
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
                Image(systemName: provider.icon)
                    .font(.title2)
                Text("Sign in to \(provider.displayName)")
                    .font(.title2.bold())
            }

            switch provider {
            case .kDrive:
                kDriveFields
            case .oneDrive:
                oneDriveFields
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
                }
                .disabled(isAuthenticating)

                Spacer()

                if isAuthenticating {
                    ProgressView()
                        .scaleEffect(0.7)
                }

                if provider != .oneDrive || appState.syncManager.oneDriveDeviceCode == nil {
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

    private var isLoginDisabled: Bool {
        if isAuthenticating { return true }
        switch selectedProvider {
        case .kDrive: return apiToken.isEmpty
        case .oneDrive: return false
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
