import SwiftUI

struct SettingsView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        TabView {
            GeneralSettingsView()
                .tabItem {
                    Label("General", systemImage: "gear")
                }

            CloudSettingsView()
                .tabItem {
                    Label("Cloud Accounts", systemImage: "cloud")
                }
        }
        .frame(width: 500, height: 350)
    }
}

struct GeneralSettingsView: View {
    @AppStorage("showHiddenFiles") private var showHiddenFiles = false
    @AppStorage("confirmDelete") private var confirmDelete = true

    var body: some View {
        Form {
            Toggle("Show hidden files by default", isOn: $showHiddenFiles)
            Toggle("Confirm before deleting", isOn: $confirmDelete)
        }
        .formStyle(.grouped)
    }
}

struct CloudSettingsView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        Form {
            if appState.syncManager.accounts.isEmpty {
                ContentUnavailableView(
                    "No Cloud Accounts",
                    systemImage: "cloud",
                    description: Text("Add a cloud account from the sidebar.")
                )
            } else {
                ForEach(appState.syncManager.accounts) { account in
                    HStack {
                        CloudProviderIcon(providerType: account.providerType, size: 16)
                        Text(account.displayName)
                        Spacer()
                        Circle()
                            .fill(account.isConnected ? .green : .gray)
                            .frame(width: 8, height: 8)
                        Button("Remove", role: .destructive) {
                            Task { await appState.syncManager.removeAccount(account) }
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}

