import SwiftUI
import FileFlussCore

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
    @AppStorage("showSidebarAddAccount") private var showSidebarAddAccount = true

    // Section expansion is tracked per panel side so users can have different
    // sections collapsed on left vs right. The chooser between the two
    // `@AppStorage` properties below switches at view-creation time based
    // on `panelSide`, and the bindings forwarded to `Section(isExpanded:)`
    // resolve to the correct underlying key.
    @AppStorage("sidebar.left.section.favorites.expanded") private var favoritesExpandedLeft = true
    @AppStorage("sidebar.left.section.drives.expanded") private var drivesExpandedLeft = true
    @AppStorage("sidebar.left.section.cloud.expanded") private var cloudExpandedLeft = true
    @AppStorage("sidebar.right.section.favorites.expanded") private var favoritesExpandedRight = true
    @AppStorage("sidebar.right.section.drives.expanded") private var drivesExpandedRight = true
    @AppStorage("sidebar.right.section.cloud.expanded") private var cloudExpandedRight = true

    private var favoritesExpanded: Binding<Bool> {
        panelSide == .left ? $favoritesExpandedLeft : $favoritesExpandedRight
    }
    private var drivesExpanded: Binding<Bool> {
        panelSide == .left ? $drivesExpandedLeft : $drivesExpandedRight
    }
    private var cloudExpanded: Binding<Bool> {
        panelSide == .left ? $cloudExpandedLeft : $cloudExpandedRight
    }

    private var selection: Binding<SidebarItem?> {
        Binding(
            get: { appState.sidebarSelection(for: panelSide) },
            set: { newValue in
                let previous = appState.sidebarSelection(for: panelSide)
                appState.setSidebarSelection(newValue, for: panelSide)
                // Selecting a cloud account in the sidebar should always land
                // at the account root. Without this, re-clicking the account
                // keeps the last-opened folder (SwiftUI skips onChange when
                // the value is unchanged, so we handle the same-click case
                // here explicitly).
                if case .cloudAccount(let account) = newValue, previous == newValue {
                    Task {
                        let cloudFM = appState.cloudFileManager(for: account.id, side: panelSide)
                        let target = account.rootPath.isEmpty ? "/" : account.rootPath
                        await cloudFM.navigateTo(target)
                    }
                }
            }
        )
    }

    @State private var renamingFavorite: SidebarFavorite?
    @State private var renameText: String = ""
    @State private var renamingAccountId: UUID?
    @State private var renameAccountText: String = ""

    /// "Change Icon" submenu shown in the favorite's context menu. Lists
    /// the matching cloud provider's logo (when this is a cloud favorite)
    /// and a curated set of SF Symbols.
    @ViewBuilder
    private func changeIconMenu(for fav: SidebarFavorite) -> some View {
        Menu {
            if fav.kind == .cloudFolder,
               let providerType = fav.providerType,
               let asset = providerType.logoAssetName,
               let menuImage = Self.menuSizedImage(named: asset) {
                Button {
                    appState.setFavoriteIcon(id: fav.id, to: .favoriteAssetIcon(asset), in: panelSide)
                } label: {
                    Label {
                        Text("\(providerType.displayName) Logo")
                    } icon: {
                        // AppKit menus ignore SwiftUI .frame() on Image,
                        // so we hand them an NSImage whose intrinsic size
                        // is already 16×16.
                        Image(nsImage: menuImage)
                    }
                }
                Divider()
            }
            ForEach(FavoriteIconLibrary.allSymbols, id: \.self) { item in
                Button {
                    appState.setFavoriteIcon(id: fav.id, to: item.symbol, in: panelSide)
                } label: {
                    Label(item.name, systemImage: item.symbol)
                }
            }
        } label: {
            Text("Change Icon")
        }
    }

    /// Loads an asset-catalog image and returns a 16×16 NSImage copy.
    /// The original image data is preserved; only the layout size hint
    /// changes, which is what NSMenuItem honours.
    private static func menuSizedImage(named asset: String) -> NSImage? {
        guard let original = NSImage(named: asset) else { return nil }
        // Copy so we don't mutate the cached app-wide instance.
        let copy = original.copy() as? NSImage ?? original
        copy.size = NSSize(width: 16, height: 16)
        return copy
    }

    @ViewBuilder
    private func favoriteRow(_ fav: SidebarFavorite) -> some View {
        switch fav.kind {
        case .localPath:
            if let url = fav.url {
                Label {
                    Text(fav.displayName)
                } icon: {
                    FavoriteIconView(icon: fav.icon)
                }
                .tag(SidebarItem.location(url))
            }
        case .cloudFolder:
            if let accountId = fav.accountId, let path = fav.cloudPath {
                Label {
                    Text(fav.displayName)
                } icon: {
                    FavoriteIconView(icon: fav.icon)
                }
                .tag(SidebarItem.cloudFolder(accountId: accountId, path: path))
            }
        }
    }

    var body: some View {
        List(selection: selection) {
            Section(isExpanded: favoritesExpanded) {
                ForEach(appState.favorites(for: panelSide)) { fav in
                    favoriteRow(fav)
                        .contextMenu {
                            Button("Rename") {
                                renameText = fav.displayName
                                renamingFavorite = fav
                            }
                            changeIconMenu(for: fav)
                            Divider()
                            Button("Remove from Favorites", role: .destructive) {
                                appState.removeFavorite(id: fav.id, from: panelSide)
                            }
                        }
                }
                .onMove { indices, destination in
                    appState.moveFavorites(in: panelSide, fromOffsets: indices, toOffset: destination)
                }
            } header: {
                Text("Favorites")
            }

            if !appState.driveMonitor.drives.isEmpty {
                Section(isExpanded: drivesExpanded) {
                    ForEach(appState.driveMonitor.drives) { drive in
                        DriveRow(drive: drive, panelSide: panelSide)
                            .tag(SidebarItem.drive(driveId: drive.id))
                            .contextMenu {
                                driveContextMenu(for: drive)
                            }
                    }
                } header: {
                    Text("Drives")
                }
            }

            Section(isExpanded: cloudExpanded) {
                    ForEach(appState.syncManager.accounts) { account in
                        Label {
                            HStack {
                                Text(account.displayName)
                                    // Italic + secondary when the user has
                                    // forced offline mode, matching how
                                    // unmounted drives render. Disconnected
                                    // accounts also dim (no italic) to
                                    // distinguish "we lost the connection"
                                    // from "the user chose to go offline".
                                    .italic(account.isOfflineMode)
                                    .foregroundStyle(
                                        account.isOfflineMode || !account.isConnected
                                            ? .secondary
                                            : .primary
                                    )
                                Spacer()
                                if let job = appState.indexingService.activeJob(sourceId: account.id.uuidString) {
                                    indexingProgress(processed: job.filesProcessed)
                                } else if account.isOfflineMode {
                                    Image(systemName: "wifi.slash")
                                        .font(.caption2)
                                        .foregroundStyle(.secondary)
                                } else {
                                    Circle()
                                        .fill(account.isConnected ? .green : .gray)
                                        .frame(width: 8, height: 8)
                                }
                            }
                        } icon: {
                            CloudProviderIcon(providerType: account.providerType, size: 16)
                        }
                        .tag(SidebarItem.cloudAccount(account))
                        .contextMenu {
                            Button("Rename...") {
                                renamingAccountId = account.id
                                renameAccountText = account.displayName
                            }
                            Divider()
                            offlineModeMenu(for: account)
                            Divider()
                            cloudIndexMenu(for: account)
                        }
                    }
                    .onMove { indices, destination in
                        appState.syncManager.accounts.move(fromOffsets: indices, toOffset: destination)
                        appState.syncManager.saveAccounts()
                    }

                    if showSidebarAddAccount {
                        Button {
                            appState.syncManager.isAddingAccount = true
                        } label: {
                            Label("Add Cloud Account…", systemImage: "plus.circle")
                                .foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                    }
                } header: {
                    Text("Cloud Accounts")
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
                    let cloudFM = appState.cloudFileManager(for: accountId, side: panelSide)
                    await cloudFM.navigateTo(path)
                }
            case .cloudAccount(let account):
                Task {
                    let cloudFM = appState.cloudFileManager(for: account.id, side: panelSide)
                    let target = account.rootPath.isEmpty ? "/" : account.rootPath
                    await cloudFM.navigateTo(target)
                }
            case .drive(let driveId):
                // Online drives navigate the local file manager to the
                // mount path; offline drives are handled by the panel
                // content via OfflineSourceView.
                if let mount = appState.driveMonitor.mountURL(for: driveId) {
                    Task {
                        await appState.fileManager(for: panelSide).navigateTo(mount)
                    }
                }
            default:
                break
            }
        }
        .alert("Rename Favorite", isPresented: Binding(
            get: { renamingFavorite != nil },
            set: { if !$0 { renamingFavorite = nil } }
        )) {
            TextField("Name", text: $renameText)
            Button("Rename") {
                if let fav = renamingFavorite, !renameText.isEmpty {
                    appState.renameFavorite(id: fav.id, to: renameText, in: panelSide)
                }
                renamingFavorite = nil
            }
            Button("Cancel", role: .cancel) {
                renamingFavorite = nil
            }
        } message: {
            Text("Enter a new name for this favorite.")
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

    // MARK: - Drive / indexing helpers

    /// Spinner + count shown next to a drive or cloud account while a
    /// background indexing job is running.
    @ViewBuilder
    private func indexingProgress(processed: Int) -> some View {
        HStack(spacing: 4) {
            ProgressView().controlSize(.mini)
            Text("\(processed)")
                .font(.caption2)
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func driveContextMenu(for drive: Drive) -> some View {
        let isOnline = appState.driveMonitor.isOnline(drive)
        let activeJob = appState.indexingService.activeJob(sourceId: drive.id)
        // Show the indexed-at info as a disabled label so users can decide
        // whether a re-index is needed before triggering one.
        if let last = drive.lastIndexed {
            Text("Indexed \(Self.shortIndexedSummary(date: last, files: drive.totalFiles))")
            Divider()
        } else {
            Text("Not indexed yet")
            Divider()
        }
        if isOnline, let mount = appState.driveMonitor.mountURL(for: drive.id) {
            if activeJob == nil {
                Button(drive.lastIndexed == nil ? "Index for Offline Search…" : "Re-index for Offline Search…") {
                    appState.indexingService.indexDrive(drive, mountURL: mount)
                }
            } else {
                Button("Cancel Indexing") {
                    appState.indexingService.cancel(sourceId: drive.id)
                }
            }
            Divider()
        }
        if drive.lastIndexed != nil {
            Button("Remove Offline Index", role: .destructive) {
                Task {
                    await SearchIndex.shared.dropSource(sourceId: drive.id)
                    var d = drive
                    d.lastIndexed = nil
                    d.totalFiles = 0
                    d.totalBytes = 0
                    if isOnline {
                        appState.driveMonitor.upsert(d)
                    } else {
                        appState.driveMonitor.forget(driveId: drive.id)
                    }
                }
            }
        } else if !isOnline {
            Button("Forget Drive", role: .destructive) {
                appState.driveMonitor.forget(driveId: drive.id)
            }
        }
    }

    /// Right-click → Go Offline / Go Online toggle. Going offline routes
    /// the panel to `OfflineSourceView`, which reads the cached file/folder
    /// listing from `cloud_files`. The option is only meaningful when the
    /// account has actually been indexed end-to-end (presence in
    /// `cloudIndexInfo`); offering it on a never-indexed account would land
    /// the user on an empty offline view with no obvious fix.
    @ViewBuilder
    private func offlineModeMenu(for account: CloudAccount) -> some View {
        let hasFullIndex = appState.cloudIndexInfo[account.id] != nil
        if account.isOfflineMode {
            Button("Go Online") {
                appState.syncManager.setOfflineMode(false, accountId: account.id)
            }
        } else if hasFullIndex {
            Button("Go Offline") {
                appState.syncManager.setOfflineMode(true, accountId: account.id)
            }
            .help("Browse and search this account's last-indexed contents without contacting the server.")
        } else {
            Button("Go Offline") {}
                .disabled(true)
                .help("Index this account for offline use first.")
        }
    }

    @ViewBuilder
    private func cloudIndexMenu(for account: CloudAccount) -> some View {
        let activeJob = appState.indexingService.activeJob(sourceId: account.id.uuidString)
        let lastIndexed = appState.cloudIndexInfo[account.id]
        if let info = lastIndexed {
            Text("Indexed \(Self.shortIndexedSummary(date: info.lastIndexed, files: info.totalFiles))")
            Divider()
        } else {
            // Distinct from "Never indexed" — covers both the
            // truly-untouched case and the "you've browsed a few folders
            // but never ran a full crawl" case, since the per-folder
            // upserts shouldn't be advertised as a complete index.
            Text("Not fully indexed yet")
            Divider()
        }
        if account.isConnected {
            if activeJob == nil {
                Button(lastIndexed == nil ? "Index for Offline Search…" : "Re-index for Offline Search…") {
                    appState.indexingService.indexCloudAccount(account)
                }
            } else {
                Button("Cancel Indexing") {
                    appState.indexingService.cancel(sourceId: account.id.uuidString)
                }
            }
            if lastIndexed != nil {
                Divider()
                Button("Remove Offline Index", role: .destructive) {
                    let accountId = account.id
                    Task {
                        await SearchIndex.shared.dropCloudAccount(accountId: accountId)
                        await SearchIndex.shared.dropSource(sourceId: accountId.uuidString)
                        await appState.refreshIndexInfo()
                    }
                }
            }
        }
    }

    /// e.g. "today · 12,304 files" or "3 days ago · 12,304 files"
    private static func shortIndexedSummary(date: Date, files: Int) -> String {
        let secs = Date().timeIntervalSince(date)
        let when: String
        if secs < 60 { when = "just now" }
        else if secs < 86_400 { when = "today" }
        else if secs < 86_400 * 2 { when = "yesterday" }
        else { when = "\(Int(secs / 86_400)) days ago" }
        let countStr = NumberFormatter.localizedString(from: NSNumber(value: files), number: .decimal)
        return "\(when) · \(countStr) files"
    }
}

// MARK: - Drive sidebar row

private struct DriveRow: View {
    let drive: Drive
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState

    private var isOnline: Bool { appState.driveMonitor.isOnline(drive) }

    var body: some View {
        Label {
            HStack(spacing: 6) {
                Text(drive.displayName)
                    .italic(!isOnline)
                    .foregroundStyle(isOnline ? .primary : .secondary)
                Spacer()
                if let job = appState.indexingService.activeJob(sourceId: drive.id) {
                    HStack(spacing: 4) {
                        ProgressView().controlSize(.mini)
                        Text("\(job.filesProcessed)")
                            .font(.caption2)
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                    }
                } else if !isOnline {
                    Text("Offline")
                        .font(.caption2)
                        .padding(.horizontal, 5)
                        .padding(.vertical, 1)
                        .background(Color.secondary.opacity(0.18), in: Capsule())
                        .foregroundStyle(.secondary)
                }
            }
        } icon: {
            Image(systemName: drive.kind.sfSymbol)
                .foregroundStyle(isOnline ? Color.accentColor : Color.secondary)
        }
    }
}

// MARK: - Transfer Row

private struct TransferRow: View {
    let transfer: TransferProgress
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState
    @State private var showDetails = false
    @State private var showCancelConfirmation = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                if let details = transfer.completionDetailNames, transfer.isComplete {
                    Text(transfer.statusText)
                        .font(.caption)
                        .lineLimit(1)
                        .help(details)
                } else {
                    Text(transfer.statusText)
                        .font(.caption)
                        .lineLimit(1)
                }
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
                } else if !transfer.isCancelled {
                    Button {
                        showCancelConfirmation = true
                    } label: {
                        Text("Cancel")
                            .font(.caption2)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help("Cancel this transfer")
                }
            }

            CapsuleProgressBar(transfer: transfer)
                .frame(height: 18)

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
        .confirmationDialog(
            "Cancel this transfer?",
            isPresented: $showCancelConfirmation,
            titleVisibility: .visible
        ) {
            Button("Cancel Transfer", role: .destructive) {
                transfer.cancel()
            }
            Button("Keep Running", role: .cancel) {}
        } message: {
            Text("Files already transferred will remain. Any partial file currently in flight will be discarded.")
        }
    }
}

// MARK: - Capsule Progress Bar

private struct CapsuleProgressBar: View {
    let transfer: TransferProgress

    private var tintGradient: LinearGradient {
        let colors: [Color]
        if transfer.isComplete {
            if transfer.hasErrors {
                // Partial success gets orange, full failure gets red so the
                // user can tell at a glance whether anything got through.
                colors = transfer.successCount > 0
                    ? [Color.orange.opacity(0.85), Color.orange]
                    : [Color.red.opacity(0.85), Color.red]
            } else {
                colors = [Color.green.opacity(0.85), Color.green]
            }
        } else if transfer.isCloudToCloud {
            colors = transfer.currentPhase == .downloading
                ? [Color.blue.opacity(0.85), Color.cyan]
                : [Color.purple.opacity(0.85), Color.pink.opacity(0.9)]
        } else if transfer.isCloudUpload {
            colors = [Color.purple.opacity(0.85), Color.pink.opacity(0.9)]
        } else {
            colors = [Color.blue.opacity(0.85), Color.cyan]
        }
        return LinearGradient(colors: colors, startPoint: .leading, endPoint: .trailing)
    }

    var body: some View {
        GeometryReader { geo in
            let fraction = max(0, min(1, transfer.fraction))
            let filledWidth = geo.size.width * fraction

            ZStack(alignment: .leading) {
                // Track
                Capsule()
                    .fill(Color.primary.opacity(0.08))
                    .overlay(
                        Capsule()
                            .strokeBorder(Color.primary.opacity(0.05), lineWidth: 0.5)
                    )

                // Fill
                Capsule()
                    .fill(tintGradient)
                    .frame(width: filledWidth)
                    .animation(.easeOut(duration: 0.15), value: fraction)

                // Percentage label, centered in the bar
                Text(transfer.percentText)
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(fraction > 0.55 ? Color.white : Color.primary.opacity(0.75))
                    .frame(maxWidth: .infinity, alignment: .center)
                    .shadow(color: fraction > 0.55 ? .black.opacity(0.15) : .clear, radius: 0.5, y: 0.5)
            }
        }
    }
}

private struct TransferDetailsView: View {
    let transfer: TransferProgress

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Transfer Details")
                .font(.headline)

            if let errorMessage = transfer.errorMessage {
                HStack(alignment: .top, spacing: 4) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                    Text(errorMessage)
                        .foregroundStyle(.red)
                        .font(.callout)
                        .textSelection(.enabled)
                }
                .padding(.vertical, 2)
            }

            if transfer.failureCount > 0 || transfer.successCount > 0 {
                HStack(spacing: 10) {
                    if transfer.successCount > 0 {
                        Label("\(transfer.successCount) succeeded", systemImage: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                    }
                    if transfer.failureCount > 0 {
                        Label("\(transfer.failureCount) failed", systemImage: "xmark.octagon.fill")
                            .foregroundStyle(.red)
                    }
                    if transfer.skippedCount > 0 {
                        Label("\(transfer.skippedCount) skipped", systemImage: "minus.circle.fill")
                            .foregroundStyle(.secondary)
                    }
                }
                .font(.caption)
            }

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
                } else if transfer.isCloudDownload {
                    LabeledContent("Download Speed") {
                        Text(transfer.averageSpeed)
                    }
                } else if transfer.isCloudUpload {
                    LabeledContent("Upload Speed") {
                        Text(transfer.averageSpeed)
                    }
                } else {
                    LabeledContent("Avg. Speed") {
                        Text(transfer.averageSpeed)
                    }
                }
            }

            Divider()

            itemsList
        }
        .padding()
        .frame(width: 340)
    }

    @ViewBuilder
    private var itemsList: some View {
        if !transfer.itemResults.isEmpty {
            Text("Items (\(transfer.itemResults.count))")
                .font(.subheadline)
                .foregroundStyle(.secondary)

            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(transfer.itemResults) { result in
                        TransferItemRow(result: result)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 220)
        } else if !transfer.transferredFileNames.isEmpty {
            // Fallback for transfers that didn't record per-item results
            // (e.g. older code paths still in flight).
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
    }
}

private struct TransferItemRow: View {
    let result: TransferItemResult

    private var icon: (name: String, color: Color) {
        switch result.status {
        case .succeeded: return ("checkmark.circle.fill", .green)
        case .failed: return ("xmark.octagon.fill", .red)
        case .skipped: return ("minus.circle.fill", .secondary)
        case .cancelled: return ("slash.circle.fill", .secondary)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 6) {
                Image(systemName: icon.name)
                    .foregroundStyle(icon.color)
                    .font(.caption)
                Text(result.name)
                    .font(.caption)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(result.name)
            }
            if let error = result.errorMessage {
                Text(error)
                    .font(.caption2)
                    .foregroundStyle(.red)
                    .lineLimit(2)
                    .padding(.leading, 18)
                    .textSelection(.enabled)
            }
        }
    }
}
