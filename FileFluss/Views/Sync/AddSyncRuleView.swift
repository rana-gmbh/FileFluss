import SwiftUI

struct AddSyncRuleView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    @State private var localPath: URL?
    @State private var remotePath: String = "/"
    @State private var selectedAccountId: UUID?
    @State private var direction: SyncDirection = .bidirectional

    var body: some View {
        VStack(spacing: 0) {
            Text("New Sync Rule")
                .font(.title2.bold())
                .padding()

            Form {
                Section("Local Folder") {
                    HStack {
                        if let localPath {
                            Text(localPath.path())
                                .lineLimit(1)
                                .truncationMode(.head)
                        } else {
                            Text("Select a folder...")
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Browse...") {
                            selectFolder()
                        }
                    }
                }

                Section("Cloud Destination") {
                    Picker("Account", selection: $selectedAccountId) {
                        Text("Select account...").tag(nil as UUID?)
                        ForEach(appState.syncManager.accounts) { account in
                            Text(account.displayName).tag(account.id as UUID?)
                        }
                    }

                    TextField("Remote Path", text: $remotePath)
                }

                Section("Direction") {
                    Picker("Sync Direction", selection: $direction) {
                        ForEach(SyncDirection.allCases, id: \.self) { dir in
                            Label(dir.displayName, systemImage: dir.icon)
                                .tag(dir)
                        }
                    }
                    .pickerStyle(.segmented)
                }
            }
            .formStyle(.grouped)

            HStack {
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Spacer()
                Button("Create Rule") {
                    guard let localPath, let selectedAccountId else { return }
                    appState.syncManager.addSyncRule(
                        localPath: localPath,
                        remotePath: remotePath,
                        accountId: selectedAccountId,
                        direction: direction
                    )
                    dismiss()
                }
                .buttonStyle(.borderedProminent)
                .disabled(localPath == nil || selectedAccountId == nil)
                .keyboardShortcut(.defaultAction)
            }
            .padding()
        }
        .frame(width: 480, height: 420)
    }

    private func selectFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.message = "Select a folder to sync"
        if panel.runModal() == .OK {
            localPath = panel.url
        }
    }
}
