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
    @State private var renamingCloudFavorite: CloudFavorite?
    @State private var renameCloudText: String = ""
    @State private var renamingAccountId: UUID?
    @State private var renameAccountText: String = ""

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

                ForEach(appState.cloudFavorites) { fav in
                    Label(fav.displayName, systemImage: fav.icon)
                        .tag(SidebarItem.cloudFolder(accountId: fav.accountId, path: fav.path))
                        .contextMenu {
                            Button("Rename") {
                                renameCloudText = fav.displayName
                                renamingCloudFavorite = fav
                            }
                            Divider()
                            Button("Remove from Favorites", role: .destructive) {
                                appState.removeCloudFavorite(id: fav.id)
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
                        .contextMenu {
                            Button("Rename...") {
                                renamingAccountId = account.id
                                renameAccountText = account.displayName
                            }
                        }
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

            if !appState.transfers(for: panelSide).isEmpty {
                Section("Transfers") {
                    ForEach(appState.transfers(for: panelSide)) { transfer in
                        TransferRow(transfer: transfer, panelSide: panelSide)
                    }
                }
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
                                appState.removeFolderSize(id: entry.id, panel: panelSide)
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
            switch newValue {
            case .location(let url):
                Task {
                    await appState.fileManager(for: panelSide).navigateTo(url)
                }
            case .cloudFolder(let accountId, let path):
                Task {
                    let cloudFM = appState.cloudFileManager(for: accountId)
                    await cloudFM.navigateTo(path)
                }
            default:
                break
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
        .alert("Rename Cloud Favorite", isPresented: Binding(
            get: { renamingCloudFavorite != nil },
            set: { if !$0 { renamingCloudFavorite = nil } }
        )) {
            TextField("Name", text: $renameCloudText)
            Button("Rename") {
                if let fav = renamingCloudFavorite, !renameCloudText.isEmpty {
                    appState.renameCloudFavorite(id: fav.id, to: renameCloudText)
                }
                renamingCloudFavorite = nil
            }
            Button("Cancel", role: .cancel) {
                renamingCloudFavorite = nil
            }
        } message: {
            Text("Enter a new name for this cloud favorite.")
        }
        .alert("Rename Cloud Account", isPresented: Binding(
            get: { renamingAccountId != nil },
            set: { if !$0 { renamingAccountId = nil } }
        )) {
            TextField("Name", text: $renameAccountText)
            Button("Rename") {
                if let accountId = renamingAccountId, !renameAccountText.isEmpty {
                    appState.syncManager.renameAccount(id: accountId, to: renameAccountText)
                }
                renamingAccountId = nil
            }
            Button("Cancel", role: .cancel) {
                renamingAccountId = nil
            }
        } message: {
            Text("Enter a new name for this cloud account.")
        }
    }
}

// MARK: - Transfer Row

private struct TransferRow: View {
    let transfer: TransferProgress
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState
    @State private var showDetails = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(transfer.statusText)
                    .font(.caption)
                    .lineLimit(1)
                Spacer()
                if transfer.isComplete {
                    Button("Details") {
                        showDetails = true
                    }
                    .font(.caption2)
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    Button {
                        appState.removeTransfer(id: transfer.id, panel: panelSide)
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.tertiary)
                    }
                    .buttonStyle(.plain)
                }
            }
            ProgressView(value: transfer.fraction)
                .progressViewStyle(.linear)
            if !transfer.currentFileName.isEmpty && !transfer.isComplete {
                Text(transfer.currentFileName)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .popover(isPresented: $showDetails) {
            TransferDetailsView(transfer: transfer)
        }
    }
}

private struct TransferDetailsView: View {
    let transfer: TransferProgress

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Transfer Details")
                .font(.headline)

            Divider()

            LabeledContent("Operation") {
                Text(transfer.operation)
            }
            LabeledContent("Finished") {
                Text(transfer.formattedEndTime)
            }
            if transfer.totalBytes > 0 {
                LabeledContent("Total Size") {
                    Text(ByteCountFormatter.string(fromByteCount: transfer.totalBytes, countStyle: .file))
                }
                if transfer.isCloudToCloud {
                    LabeledContent("Download Speed") {
                        Text(transfer.downloadSpeed)
                    }
                    LabeledContent("Upload Speed") {
                        Text(transfer.uploadSpeed)
                    }
                } else {
                    LabeledContent("Avg. Speed") {
                        Text(transfer.averageSpeed)
                    }
                }
            }

            Divider()

            Text("Items (\(transfer.transferredFileNames.count))")
                .font(.subheadline)
                .foregroundStyle(.secondary)

            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(transfer.transferredFileNames, id: \.self) { name in
                        Text(name)
                            .font(.caption)
                            .lineLimit(1)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 150)
        }
        .padding()
        .frame(width: 280)
    }
}
