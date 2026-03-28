import SwiftUI

struct SidebarView: View {
    @Binding var selection: SidebarItem?
    @Environment(AppState.self) private var appState

    private let favorites: [(String, String, URL)] = {
        let fm = FileManager.default
        let home = fm.homeDirectoryForCurrentUser
        return [
            ("Home", "house", home),
            ("Desktop", "menubar.dock.rectangle", home.appendingPathComponent("Desktop")),
            ("Documents", "doc", home.appendingPathComponent("Documents")),
            ("Downloads", "arrow.down.circle", home.appendingPathComponent("Downloads")),
            ("Pictures", "photo", home.appendingPathComponent("Pictures")),
            ("Music", "music.note", home.appendingPathComponent("Music")),
        ]
    }()

    var body: some View {
        List(selection: $selection) {
            Section("Favorites") {
                ForEach(favorites, id: \.2) { name, icon, url in
                    Label(name, systemImage: icon)
                        .tag(SidebarItem.location(url))
                }
            }

            Section("Cloud Accounts") {
                ForEach(appState.syncManager.accounts) { account in
                    Label {
                        HStack {
                            Text(account.displayName)
                            Spacer()
                            Circle()
                                .fill(account.isConnected ? .green : .gray)
                                .frame(width: 8, height: 8)
                        }
                    } icon: {
                        Image(systemName: account.providerType.icon)
                    }
                    .tag(SidebarItem.cloudAccount(account))
                }

                Button {
                    appState.syncManager.isAddingAccount = true
                } label: {
                    Label("Add Account...", systemImage: "plus.circle")
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
            }

            Section("Sync") {
                Label("Sync Rules", systemImage: "arrow.triangle.2.circlepath")
                    .tag(SidebarItem.syncRules)
                    .badge(appState.syncManager.syncRules.count)
            }
        }
        .listStyle(.sidebar)
        .frame(minWidth: 200)
        .onChange(of: selection) { _, newValue in
            if case .location(let url) = newValue {
                Task {
                    await appState.fileManager.navigateTo(url)
                }
            }
        }
        .sheet(isPresented: Bindable(appState.syncManager).isAddingAccount) {
            AddCloudAccountView()
        }
    }
}
