import SwiftUI

private struct CalculatingLabel: View {
    @State private var opacity: Double = 1.0

    var body: some View {
        Text("Calculating…")
            .font(.caption)
            .foregroundStyle(.secondary)
            .opacity(opacity)
            .onAppear {
                withAnimation(.easeInOut(duration: 1.0).repeatForever(autoreverses: true)) {
                    opacity = 0.3
                }
            }
    }
}

struct SidebarView: View {
    let panelSide: PanelSide
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

    private var selection: Binding<SidebarItem?> {
        Binding(
            get: { appState.sidebarSelection(for: panelSide) },
            set: { appState.setSidebarSelection($0, for: panelSide) }
        )
    }

    @State private var renamingFavorite: FavoriteFolder?
    @State private var renameText: String = ""

    var body: some View {
        List(selection: selection) {
            Section("Favorites") {
                ForEach(favorites, id: \.2) { name, icon, url in
                    Label(name, systemImage: icon)
                        .tag(SidebarItem.location(url))
                }

                ForEach(appState.customFavorites) { fav in
                    Label(fav.displayName, systemImage: fav.icon)
                        .tag(SidebarItem.location(fav.url))
                        .contextMenu {
                            Button("Rename") {
                                renameText = fav.displayName
                                renamingFavorite = fav
                            }
                            Divider()
                            Button("Remove from Favorites", role: .destructive) {
                                appState.removeFavorite(id: fav.id)
                            }
                        }
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

            if !appState.folderSizes(for: panelSide).isEmpty {
                Section("Folder Sizes") {
                    ForEach(appState.folderSizes(for: panelSide)) { entry in
                        HStack {
                            Image(systemName: "folder.fill")
                                .foregroundStyle(.secondary)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(entry.name)
                                    .lineLimit(1)
                                if entry.isCalculating {
                                    CalculatingLabel()
                                } else {
                                    Text(entry.formattedSize)
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                            }
                            Spacer()
                            Button {
                                appState.removeFolderSize(at: entry.url, panel: panelSide)
                            } label: {
                                Image(systemName: "xmark.circle.fill")
                                    .foregroundStyle(.tertiary)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .onChange(of: appState.sidebarSelection(for: panelSide)) { _, newValue in
            if case .location(let url) = newValue {
                Task {
                    await appState.fileManager(for: panelSide).navigateTo(url)
                }
            }
        }
        .sheet(isPresented: Bindable(appState.syncManager).isAddingAccount) {
            AddCloudAccountView()
        }
        .alert("Rename Favorite", isPresented: Binding(
            get: { renamingFavorite != nil },
            set: { if !$0 { renamingFavorite = nil } }
        )) {
            TextField("Name", text: $renameText)
            Button("Rename") {
                if let fav = renamingFavorite, !renameText.isEmpty {
                    appState.renameFavorite(id: fav.id, to: renameText)
                }
                renamingFavorite = nil
            }
            Button("Cancel", role: .cancel) {
                renamingFavorite = nil
            }
        } message: {
            Text("Enter a new name for this favorite.")
        }
    }
}
