import SwiftUI
import AppKit
import FileFlussCore

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

            StorageSettingsView()
                .tabItem {
                    Label("Storage", systemImage: "internaldrive")
                }

            IndexStatusSettingsView()
                .tabItem {
                    Label("Index Status", systemImage: "magnifyingglass.circle")
                }

            KeyboardMapSettingsView()
                .tabItem {
                    Label("Keyboard Map", systemImage: "keyboard")
                }
        }
        // The hosting `Window` scene (see FileFlussApp) handles min size,
        // default size, and resizability. No NSWindow workaround needed.
    }
}

struct GeneralSettingsView: View {
    @AppStorage("showHiddenFiles") private var showHiddenFiles = false
    @AppStorage("confirmDelete") private var confirmDelete = true
    @AppStorage("showSidebarAddAccount") private var showSidebarAddAccount = true
    @AppStorage("allowSidebarRemoveAccount") private var allowSidebarRemoveAccount = false
    @AppStorage("hasCompletedWelcome") private var hasCompletedWelcome = false

    var body: some View {
        Form {
            Toggle("Show hidden files by default", isOn: $showHiddenFiles)
            Toggle("Confirm before deleting", isOn: $confirmDelete)
            Toggle("Show \"Add Cloud Account\" in sidebars", isOn: $showSidebarAddAccount)
            Toggle("Allow removing cloud accounts from sidebar context menu", isOn: $allowSidebarRemoveAccount)

            Section {
                Button("Show Welcome Screen Again") {
                    hasCompletedWelcome = false
                    NSApp.activate(ignoringOtherApps: true)
                }
            }
        }
        .formStyle(.grouped)
    }
}

struct CloudSettingsView: View {
    @Environment(AppState.self) private var appState

    /// Settings owns its own add-account sheet rather than sharing the
    /// global `appState.syncManager.isAddingAccount` flag. The same flag
    /// is bound to a sheet on `ContentView`, so flipping it from here
    /// would present two sheets at once — one anchored to the Settings
    /// window, one to the main window.
    @State private var showAddAccount = false
    @State private var editingAccount: CloudAccount?

    var body: some View {
        Group {
            if appState.syncManager.accounts.isEmpty {
                // Pulled out of the surrounding Form on purpose:
                // ContentUnavailableView inside a Form row inherits the
                // row's leading alignment, leaving the cloud icon, title,
                // and Add Account button visually anchored to the left
                // edge of the panel. Rendering it standalone with a
                // full-bleed frame lets its built-in centring take over.
                ContentUnavailableView {
                    Label("No Cloud Accounts", systemImage: "cloud")
                } description: {
                    Text("Add a cloud account to get started.")
                } actions: {
                    Button("Add Account…") {
                        showAddAccount = true
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Form {
                    Section {
                        ForEach(appState.syncManager.accounts) { account in
                            HStack {
                            CloudProviderIcon(providerType: account.providerType, size: 16)
                            Text(account.displayName)
                            Spacer()
                            Circle()
                                .fill(account.isConnected ? .green : .gray)
                                .frame(width: 8, height: 8)
                            // Mount this account as a Finder drive via a
                            // local WebDAV server. iCloud already lives in
                            // Finder, so skip it there.
                            if account.providerType != .iCloud && account.isConnected {
                                MountToggleButton(account: account)
                            }
                            // iCloud uses the OS-level sign-in — nothing
                            // editable from inside FileFluss.
                            if account.providerType != .iCloud {
                                Button("Edit…") {
                                    editingAccount = account
                                }
                            }
                            Button("Remove", role: .destructive) {
                                Task { await appState.syncManager.removeAccount(account) }
                            }
                        }
                    }
                }

                    Section {
                        Button {
                            showAddAccount = true
                        } label: {
                            Label("Add Account…", systemImage: "plus.circle")
                        }
                    }
                }
                .formStyle(.grouped)
            }
        }
        .sheet(isPresented: $showAddAccount) {
            AddCloudAccountView()
        }
        .sheet(item: $editingAccount) { account in
            EditCloudAccountView(account: account)
                .environment(appState)
        }
    }
}

/// Per-account "Mount in Finder" toggle. Wraps the lifecycle of one entry in
/// `LoopbackMountService` — starts the WebDAV server + `mount_webdav` on
/// click, and tears them down on click again. Surfaces success and failure
/// inline so the user doesn't have to look at the log.
private struct MountToggleButton: View {
    let account: CloudAccount
    @Environment(AppState.self) private var appState
    @State private var isWorking = false
    @State private var mountError: String?
    @State private var showError = false

    var body: some View {
        let activeMount = appState.mountService.mount(for: account.id)
        let mountedNow = activeMount != nil
        HStack(spacing: 6) {
            // When mounted, a "Reveal" button next to the toggle opens the
            // mount point in Finder so the user doesn't have to hunt for it.
            if let mount = activeMount {
                Button {
                    NSWorkspace.shared.activateFileViewerSelecting([mount.mountPoint])
                } label: {
                    Image(systemName: "arrow.up.right.square")
                }
                .buttonStyle(.borderless)
                .help("Reveal “\(mount.displayName)” in Finder")
            }
            Button {
                Task { await toggle(currentlyMounted: mountedNow) }
            } label: {
                HStack(spacing: 4) {
                    if isWorking {
                        ProgressView().controlSize(.small)
                    }
                    Text(mountedNow ? "Unmount" : "Mount in Finder")
                }
            }
            .disabled(isWorking)
            .help(mountedNow ? "Eject this drive from Finder" : "Open this account as a drive in Finder")
        }
        .alert("Could not mount in Finder", isPresented: $showError, presenting: mountError) { _ in
            Button("OK", role: .cancel) {}
        } message: { error in
            Text(error)
        }
    }

    private func toggle(currentlyMounted: Bool) async {
        isWorking = true
        defer { isWorking = false }
        mountError = nil
        do {
            if currentlyMounted, let mount = appState.mountService.mount(for: account.id) {
                _ = await appState.mountService.unmount(mount)
                return
            }
            guard let provider = await appState.syncManager.providerFor(accountId: account.id) else {
                mountError = "The account isn't connected — sign in first."
                showError = true
                return
            }
            _ = try await appState.mountService.mount(
                provider: provider,
                accountId: account.id,
                displayName: account.displayName
            )
        } catch {
            mountError = error.localizedDescription
            showError = true
        }
    }
}

struct StorageSettingsView: View {
    @AppStorage("cacheAutoManage") private var autoManage = true
    @AppStorage("cacheMaxSizeMB") private var maxSizeMB = CacheManager.defaultMaxSizeMB
    @AppStorage("cacheAutoDeleteDays") private var autoDeleteDays = CacheManager.defaultAutoDeleteDays

    @State private var cache = CacheManager.shared
    @State private var showClearConfirm = false
    @State private var sliderValue: Double = Double(CacheManager.defaultMaxSizeMB)
    @State private var sizeFieldText: String = String(CacheManager.defaultMaxSizeMB)

    private let dayChoices = [1, 3, 7, 14, 30]

    var body: some View {
        Form {
            Section("Cache Usage") {
                LabeledContent("Current cache size") {
                    HStack(spacing: 8) {
                        if cache.isCalculating {
                            ProgressView().controlSize(.small)
                        }
                        Text(currentSizeText)
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                    }
                }
                HStack {
                    Spacer()
                    Button("Clear Cache…", role: .destructive) {
                        showClearConfirm = true
                    }
                    .disabled(cache.isClearing || (cache.currentSize ?? 0) == 0)
                }
            }

            Section {
                Toggle("Automatically manage cache", isOn: $autoManage)
                Picker("Delete files older than", selection: $autoDeleteDays) {
                    ForEach(dayChoices, id: \.self) { d in
                        Text(d == 1 ? "1 day" : "\(d) days").tag(d)
                    }
                }
                .disabled(!autoManage)
            } header: {
                Text("Automatic Management")
            } footer: {
                Text("On launch, FileFluss removes cached previews older than the selected age and trims the cache to the size limit below.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                HStack {
                    Slider(
                        value: $sliderValue,
                        in: CacheManager.minSizeMB ... CacheManager.maxSizeMB,
                        onEditingChanged: { editing in
                            if !editing { commitSliderValue() }
                        }
                    )
                    TextField("", text: $sizeFieldText)
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 80)
                        .fixedSize()
                        .multilineTextAlignment(.trailing)
                        .onSubmit { commitFieldValue() }
                    Text("MB")
                        .foregroundStyle(.secondary)
                }
            } header: {
                Text("Cache Size Limit")
            } footer: {
                Text("When the cache exceeds this size, the oldest files are removed first. Applied on launch and when this value changes.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            FinderMountReadCacheSection()
        }
        .formStyle(.grouped)
        .confirmationDialog(
            "Clear the cache?",
            isPresented: $showClearConfirm,
            titleVisibility: .visible
        ) {
            Button("Clear Cache", role: .destructive) {
                Task { await cache.clearAll() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes all cached cloud previews and downloaded files. Files will be re-downloaded on next preview.")
        }
        .task {
            sliderValue = Double(maxSizeMB)
            sizeFieldText = String(maxSizeMB)
            await cache.refreshSize()
        }
    }

    private var currentSizeText: String {
        guard let size = cache.currentSize else { return "Calculating…" }
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }

    private func commitSliderValue() {
        let mb = Int((sliderValue / 50).rounded()) * 50
        sliderValue = Double(mb)
        maxSizeMB = mb
        sizeFieldText = String(mb)
        Task { await cache.enforceMaxSize(mb) }
    }

    private func commitFieldValue() {
        let parsed = Int(sizeFieldText) ?? maxSizeMB
        let clamped = min(max(parsed, Int(CacheManager.minSizeMB)), Int(CacheManager.maxSizeMB))
        maxSizeMB = clamped
        sliderValue = Double(clamped)
        sizeFieldText = String(clamped)
        Task { await cache.enforceMaxSize(clamped) }
    }
}

// MARK: - Index Status

private struct IndexStatusRow: Identifiable, Hashable {
    enum Origin: Hashable {
        case drive(id: String)
        case cloud(accountId: UUID)
    }
    let id: String
    let displayName: String
    let kindLabel: String
    let icon: String
    let lastIndexed: Date?
    let totalFiles: Int
    let totalFolders: Int
    let totalBytes: Int64
    let origin: Origin
}

struct IndexStatusSettingsView: View {
    @Environment(AppState.self) private var appState
    @State private var rows: [IndexStatusRow] = []
    @State private var showWipeConfirm = false
    @State private var isLoading = false

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    var body: some View {
        VStack(spacing: 0) {
            if rows.isEmpty && !isLoading {
                ContentUnavailableView {
                    Label("No Indexed Sources", systemImage: "magnifyingglass.circle")
                } description: {
                    Text("Index a drive or cloud account from its sidebar context menu to make its contents searchable while offline.")
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(rows) { row in
                        rowView(row)
                    }
                }
                .listStyle(.inset)
            }

            Divider()
            footer
        }
        .task { await load() }
    }

    @ViewBuilder
    private func rowView(_ row: IndexStatusRow) -> some View {
        HStack(spacing: 10) {
            Image(systemName: row.icon)
                .foregroundStyle(Color.accentColor)
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 2) {
                // Resolve display name live from `syncManager.accounts` so a
                // rename in the Cloud Accounts panel updates this row without
                // requiring a refresh. Drive rows and orphaned cloud rows
                // (account deleted but indexed_sources entry still present)
                // fall through to the value captured at indexing time.
                Text(currentDisplayName(for: row))
                    .font(.callout.weight(.medium))
                HStack(spacing: 6) {
                    Text(row.kindLabel)
                    Text("·")
                    if let d = row.lastIndexed {
                        Text("Indexed \(Self.dateFormatter.string(from: d))")
                    } else {
                        Text("Never indexed").italic()
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 2) {
                Text("\(formatted(row.totalFiles)) files")
                    .font(.callout.monospacedDigit())
                Text("\(formatted(row.totalFolders)) folders")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            if appState.indexingService.activeJob(sourceId: row.id) != nil {
                ProgressView().controlSize(.small)
            } else {
                Button {
                    refresh(row: row)
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .help("Re-index this source")
                .disabled(!canRefresh(row))
            }
        }
        .padding(.vertical, 4)
    }

    private var footer: some View {
        HStack {
            Button {
                refreshAll()
            } label: {
                Label("Refresh All", systemImage: "arrow.clockwise")
            }
            .disabled(rows.allSatisfy { !canRefresh($0) })

            Spacer()

            Button(role: .destructive) {
                showWipeConfirm = true
            } label: {
                Label("Erase All Indexes", systemImage: "trash")
            }
            .disabled(rows.isEmpty)
        }
        .padding(14)
        .confirmationDialog(
            "Erase all offline indexes?",
            isPresented: $showWipeConfirm,
            titleVisibility: .visible
        ) {
            Button("Erase Everything", role: .destructive) {
                Task {
                    await SearchIndex.shared.wipeAll()
                    await appState.refreshIndexInfo()
                    // Drop persisted lastIndexed bookkeeping so the Drives
                    // section no longer claims drives are indexed.
                    for var drive in appState.driveMonitor.drives {
                        drive.lastIndexed = nil
                        drive.totalFiles = 0
                        drive.totalBytes = 0
                        appState.driveMonitor.upsert(drive)
                    }
                    await load()
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the offline search index for every drive and cloud account. Indexing can be redone later.")
        }
    }

    // MARK: - Actions

    private func refresh(row: IndexStatusRow) {
        switch row.origin {
        case .drive(let id):
            guard let drive = appState.driveMonitor.drives.first(where: { $0.id == id }),
                  let mount = appState.driveMonitor.mountURL(for: id) else { return }
            appState.indexingService.indexDrive(drive, mountURL: mount)
        case .cloud(let accountId):
            guard let account = appState.syncManager.accountFor(id: accountId),
                  account.isConnected else { return }
            appState.indexingService.indexCloudAccount(account)
        }
    }

    private func refreshAll() {
        for row in rows where canRefresh(row) { refresh(row: row) }
    }

    /// For cloud rows, return the current display name from
    /// `syncManager.accounts` so rename edits in the Cloud Accounts panel
    /// surface here immediately. Drive rows and orphaned cloud rows
    /// (account removed but indexed_sources entry still present) keep the
    /// value persisted at indexing time.
    private func currentDisplayName(for row: IndexStatusRow) -> String {
        if case .cloud(let accountId) = row.origin,
           let live = appState.syncManager.accounts.first(where: { $0.id == accountId })?.displayName,
           !live.isEmpty {
            return live
        }
        return row.displayName
    }

    private func canRefresh(_ row: IndexStatusRow) -> Bool {
        switch row.origin {
        case .drive(let id):
            return appState.driveMonitor.mountURL(for: id) != nil
        case .cloud(let accountId):
            return appState.syncManager.accountFor(id: accountId)?.isConnected ?? false
        }
    }

    private func formatted(_ n: Int) -> String {
        NumberFormatter.localizedString(from: NSNumber(value: n), number: .decimal)
    }

    // MARK: - Load

    private func load() async {
        isLoading = true
        defer { isLoading = false }

        let sources = await SearchIndex.shared.listIndexedSources()
        var built: [IndexStatusRow] = []

        for src in sources {
            let isCloud = src.kind == "cloud"
            let icon: String
            let kindLabel: String
            let origin: IndexStatusRow.Origin

            // Drive rows count from `indexed_files`; cloud accounts have
            // their entries in `cloud_files`, so `sourceCounts` returns
            // nil → 0/0 for them. Pull from `cloudAccountSummary` instead
            // when the source is a cloud account.
            let files: Int
            let folders: Int
            let bytes: Int64

            if isCloud, let uuid = UUID(uuidString: src.sourceId) {
                icon = "cloud.fill"
                kindLabel = "Cloud Account"
                origin = .cloud(accountId: uuid)
                if let summary = await SearchIndex.shared.cloudAccountSummary(accountId: uuid) {
                    files = summary.totalFiles
                    folders = summary.totalFolders
                    bytes = summary.totalBytes
                } else {
                    files = src.totalFiles
                    folders = 0
                    bytes = src.totalBytes
                }
            } else {
                let drive = appState.driveMonitor.drives.first(where: { $0.id == src.sourceId })
                icon = drive?.kind.sfSymbol ?? "externaldrive.fill"
                kindLabel = drive?.kind.displayName ?? "Drive"
                origin = .drive(id: src.sourceId)
                let counts = await SearchIndex.shared.sourceCounts(src.sourceId) ?? (0, 0)
                files = counts.files
                folders = counts.folders
                bytes = src.totalBytes
            }

            built.append(IndexStatusRow(
                id: src.sourceId,
                displayName: src.displayName,
                kindLabel: kindLabel,
                icon: icon,
                lastIndexed: src.lastIndexed,
                totalFiles: files,
                totalFolders: folders,
                totalBytes: bytes,
                origin: origin
            ))
        }
        // Note: cloud accounts that have only been *browsed* (and so have
        // rows in `cloud_files` but no `indexed_sources` entry) are
        // deliberately not surfaced here. Showing them as indexed would
        // mislead the user with the (small) browsed file count; they
        // need to trigger a full crawl from the sidebar context menu to
        // appear in this list.
        rows = built.sorted { $0.displayName.lowercased() < $1.displayName.lowercased() }
    }
}



/// Settings section for the per-mount Finder-mount LRU read cache.
/// Slider stores its value in `LoopbackMountService.cacheBudgetMBKey`
/// (mirrored to `@AppStorage`) and takes effect on the next mount.
private struct FinderMountReadCacheSection: View {
    @AppStorage(LoopbackMountService.cacheBudgetMBKey) private var maxMB: Int = LoopbackMountService.defaultCacheBudgetMB
    @State private var sliderValue: Double = Double(LoopbackMountService.defaultCacheBudgetMB)
    @State private var usedBytes: Int64?

    var body: some View {
        Section {
            LabeledContent("Currently used") {
                HStack(spacing: 8) {
                    if usedBytes == nil {
                        ProgressView().controlSize(.small)
                    }
                    Text(usedDisplay)
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                }
            }
            HStack {
                Slider(
                    value: $sliderValue,
                    in: 50...5000,
                    onEditingChanged: { editing in
                        if !editing {
                            // Snap to the nearest 50 MB so the stored value
                            // stays tidy without macOS drawing tick marks.
                            let snapped = (Int(sliderValue) / 50) * 50
                            sliderValue = Double(snapped)
                            maxMB = snapped
                        }
                    }
                )
                Text("\(Int(sliderValue)) MB")
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .frame(width: 80, alignment: .trailing)
            }
        } header: {
            Text("Finder-Mount Read Cache")
        } footer: {
            Text("Each cloud account you mount in Finder keeps recently-downloaded files locally up to this size, so re-opening the same file is instant. New value applies to the next mount.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .onAppear {
            sliderValue = Double(maxMB)
            refreshUsed()
        }
    }

    private var usedDisplay: String {
        guard let bytes = usedBytes else { return "—" }
        let formatter = ByteCountFormatter()
        formatter.allowedUnits = [.useKB, .useMB, .useGB]
        formatter.countStyle = .file
        return formatter.string(fromByteCount: bytes)
    }

    private func refreshUsed() {
        let root = LoopbackMountService.readCacheBaseURL
        Task.detached(priority: .utility) {
            let total = Self.directorySize(at: root)
            await MainActor.run { self.usedBytes = total }
        }
    }

    /// Sum of every regular file under `url`. Returns 0 when the directory
    /// doesn't exist yet (no mounts have written a cache).
    nonisolated private static func directorySize(at url: URL) -> Int64 {
        guard let enumerator = FileManager.default.enumerator(
            at: url,
            includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey],
            options: [.skipsHiddenFiles]
        ) else { return 0 }
        var total: Int64 = 0
        for case let fileURL as URL in enumerator {
            let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
            if values?.isRegularFile == true, let size = values?.fileSize {
                total += Int64(size)
            }
        }
        return total
    }
}

