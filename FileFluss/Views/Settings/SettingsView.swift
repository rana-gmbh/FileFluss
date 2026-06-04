import SwiftUI
import AppKit
import FileFlussCore

/// Identifies the Settings tabs so other parts of the app (e.g. a toolbar
/// button) can deep-link straight to one via `AppState.requestedSettingsTab`.
enum SettingsTab: Hashable {
    case general, cloud, storage, index, keyboard
}

struct SettingsView: View {
    @Environment(AppState.self) private var appState
    @State private var selectedTab: SettingsTab = .general

    var body: some View {
        TabView(selection: $selectedTab) {
            GeneralSettingsView()
                .tabItem {
                    Label { LText("General") } icon: { Image(systemName: "gear") }
                }
                .tag(SettingsTab.general)

            CloudSettingsView()
                .tabItem {
                    Label { LText("Cloud Accounts") } icon: { Image(systemName: "cloud") }
                }
                .tag(SettingsTab.cloud)

            StorageSettingsView()
                .tabItem {
                    Label { LText("Storage") } icon: { Image(systemName: "internaldrive") }
                }
                .tag(SettingsTab.storage)

            IndexStatusSettingsView()
                .tabItem {
                    Label { LText("Index Status") } icon: { Image(systemName: "magnifyingglass.circle") }
                }
                .tag(SettingsTab.index)

            KeyboardMapSettingsView()
                .tabItem {
                    Label { LText("Keyboard Map") } icon: { Image(systemName: "keyboard") }
                }
                .tag(SettingsTab.keyboard)
        }
        // The hosting `Window` scene (see FileFlussApp) handles min size,
        // default size, and resizability. No NSWindow workaround needed.
        .onAppear { consumeRequestedTab() }
        .onChange(of: appState.requestedSettingsTab) { _, _ in consumeRequestedTab() }
    }

    /// Switches to a tab requested via `AppState.requestedSettingsTab` (set by
    /// a deep-link such as the toolbar's cloud button) and clears the request.
    private func consumeRequestedTab() {
        guard let tab = appState.requestedSettingsTab else { return }
        selectedTab = tab
        appState.requestedSettingsTab = nil
    }
}

struct GeneralSettingsView: View {
    @AppStorage("showHiddenFiles") private var showHiddenFiles = false
    @AppStorage("confirmDelete") private var confirmDelete = true
    @AppStorage("showSidebarAddAccount") private var showSidebarAddAccount = true
    @AppStorage("allowSidebarRemoveAccount") private var allowSidebarRemoveAccount = false
    @AppStorage(SpaceCheck.enabledKey) private var checkSpaceBeforeTransfer = false
    @AppStorage("hasCompletedWelcome") private var hasCompletedWelcome = false
    @AppStorage(AppLanguage.storageKey) private var appLanguage: String = AppLanguage.system.rawValue
    @State private var showRelaunchPrompt = false

    var body: some View {
        Form {
            Section {
                Picker(selection: $appLanguage) {
                    ForEach(AppLanguage.allCases) { language in
                        // displayName translates "System Default" (a key) and
                        // passes the native language names through unchanged.
                        Text(L10n.text(language.displayName)).tag(language.rawValue)
                    }
                } label: {
                    LText("Language")
                }
                LText("System Default follows the language selected in macOS.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack {
                    LText("The menu bar and window titles change after relaunching FileFluss.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button(action: relaunch) { LText("Relaunch") }
                }
            }
            .onChange(of: appLanguage) { _, newValue in
                // Mirror the choice into AppleLanguages so a relaunch also
                // localises the menu bar, window titles, and system dialogs —
                // those are macOS-provided and only switch with the process
                // language. In-window content already switched live.
                AppLanguage.apply(.current(from: newValue))
                showRelaunchPrompt = true
            }
            .confirmationDialog(
                L10n.text("Relaunch to finish switching the language?"),
                isPresented: $showRelaunchPrompt
            ) {
                Button(action: relaunch) { LText("Relaunch Now") }
                Button(role: .cancel) { } label: { LText("Later") }
            } message: {
                LText("The app window updated immediately. The menu bar and window titles switch after FileFluss relaunches.")
            }

            Toggle(isOn: $showHiddenFiles) { LText("Show hidden files by default") }
            Toggle(isOn: $confirmDelete) { LText("Confirm before deleting") }
            Toggle(isOn: $showSidebarAddAccount) { LText("Show \"Add Cloud Account\" in sidebars") }
            Toggle(isOn: $allowSidebarRemoveAccount) { LText("Allow removing cloud accounts from sidebar context menu") }

            Section {
                Toggle(isOn: $checkSpaceBeforeTransfer) { LText("Check available space before copy or move") }
                LText("Warns before a transfer that would exceed the destination's storage quota or free disk space. Calculating sizes can add a noticeable delay, so this is off by default for faster file operations.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                Button(action: {
                    hasCompletedWelcome = false
                    NSApp.activate(ignoringOtherApps: true)
                }) { LText("Show Welcome Screen Again") }
            }
        }
        .formStyle(.grouped)
    }

    /// Relaunches the app so the menu bar, window titles, and system dialogs
    /// pick up the newly selected language (in-window content already switched
    /// live via LocalizedRoot).
    private func relaunch() {
        let url = Bundle.main.bundleURL
        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: url, configuration: config) { _, _ in
            Task { @MainActor in NSApp.terminate(nil) }
        }
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
    @State private var renamingAccount: CloudAccount?
    @State private var renameText: String = ""

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
                    Label(L10n.text("No Cloud Accounts"), systemImage: "cloud")
                } description: {
                    LText("Add a cloud account to get started.")
                } actions: {
                    Button(L10n.text("Add Account…")) {
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
                                Button(L10n.text("Edit…")) {
                                    editingAccount = account
                                }
                            }
                            Button(L10n.text("Rename…")) {
                                renameText = account.displayName
                                renamingAccount = account
                            }
                            Button(L10n.text("Remove"), role: .destructive) {
                                Task { await appState.syncManager.removeAccount(account) }
                            }
                        }
                    }
                }

                    Section {
                        Button {
                            showAddAccount = true
                        } label: {
                            Label(L10n.text("Add Account…"), systemImage: "plus.circle")
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
        .alert(L10n.text("Rename Account"), isPresented: Binding(
            get: { renamingAccount != nil },
            set: { if !$0 { renamingAccount = nil } }
        )) {
            TextField(L10n.text("Name"), text: $renameText)
            Button(L10n.text("Rename")) {
                if let account = renamingAccount {
                    let newName = renameText.trimmingCharacters(in: .whitespacesAndNewlines)
                    if !newName.isEmpty {
                        appState.syncManager.renameAccount(id: account.id, to: newName)
                    }
                }
                renamingAccount = nil
            }
            Button(L10n.text("Cancel"), role: .cancel) { renamingAccount = nil }
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
                    Text(mountedNow ? L10n.text("Unmount") : L10n.text("Mount in Finder"))
                }
            }
            .disabled(isWorking)
            .help(mountedNow ? L10n.text("Eject this drive from Finder") : L10n.text("Open this account as a drive in Finder"))
        }
        .alert(L10n.text("Could not mount in Finder"), isPresented: $showError, presenting: mountError) { _ in
            Button(L10n.text("OK"), role: .cancel) {}
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
                mountError = L10n.text("The account isn't connected — sign in first.")
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
            Section(L10n.text("Cache Usage")) {
                LabeledContent(L10n.text("Current cache size")) {
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
                    Button(L10n.text("Clear Cache…"), role: .destructive) {
                        showClearConfirm = true
                    }
                    .disabled(cache.isClearing || (cache.currentSize ?? 0) == 0)
                }
            }

            Section {
                Toggle(L10n.text("Automatically manage cache"), isOn: $autoManage)
                Picker(L10n.text("Delete files older than"), selection: $autoDeleteDays) {
                    ForEach(dayChoices, id: \.self) { d in
                        Text(d == 1 ? L10n.text("1 day") : L10n.format("%d days", d)).tag(d)
                    }
                }
                .disabled(!autoManage)
            } header: {
                LText("Automatic Management")
            } footer: {
                LText("On launch, FileFluss removes cached previews older than the selected age and trims the cache to the size limit below.")
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
                    LText("MB")
                        .foregroundStyle(.secondary)
                }
            } header: {
                LText("Cache Size Limit")
            } footer: {
                LText("When the cache exceeds this size, the oldest files are removed first. Applied on launch and when this value changes.")
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
            Button(L10n.text("Clear Cache"), role: .destructive) {
                Task { await cache.clearAll() }
            }
            Button(L10n.text("Cancel"), role: .cancel) {}
        } message: {
            LText("This removes all cached cloud previews and downloaded files. Files will be re-downloaded on next preview.")
        }
        .task {
            sliderValue = Double(maxSizeMB)
            sizeFieldText = String(maxSizeMB)
            await cache.refreshSize()
        }
    }

    private var currentSizeText: String {
        guard let size = cache.currentSize else { return L10n.text("Calculating…") }
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
                    Label(L10n.text("No Indexed Sources"), systemImage: "magnifyingglass.circle")
                } description: {
                    LText("Index a drive or cloud account from its sidebar context menu to make its contents searchable while offline.")
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
                    LText("·")
                    if let d = row.lastIndexed {
                        Text(L10n.format("Indexed %@", Self.dateFormatter.string(from: d)))
                    } else {
                        LText("Never indexed").italic()
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 2) {
                Text(L10n.format("%@ files", formatted(row.totalFiles)))
                    .font(.callout.monospacedDigit())
                Text(L10n.format("%@ folders", formatted(row.totalFolders)))
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
                .help(L10n.text("Re-index this source"))
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
                Label(L10n.text("Refresh All"), systemImage: "arrow.clockwise")
            }
            .disabled(rows.allSatisfy { !canRefresh($0) })

            Spacer()

            Button(role: .destructive) {
                showWipeConfirm = true
            } label: {
                Label(L10n.text("Erase All Indexes"), systemImage: "trash")
            }
            .disabled(rows.isEmpty)
        }
        .padding(14)
        .confirmationDialog(
            "Erase all offline indexes?",
            isPresented: $showWipeConfirm,
            titleVisibility: .visible
        ) {
            Button(L10n.text("Erase Everything"), role: .destructive) {
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
            Button(L10n.text("Cancel"), role: .cancel) {}
        } message: {
            LText("This removes the offline search index for every drive and cloud account. Indexing can be redone later.")
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
                kindLabel = L10n.text("Cloud Account")
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
                kindLabel = drive?.kind.displayName ?? L10n.text("Drive")
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
            LabeledContent(L10n.text("Currently used")) {
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
                Text(L10n.format("%d MB", Int(sliderValue)))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .frame(width: 80, alignment: .trailing)
            }
        } header: {
            LText("Finder-Mount Read Cache")
        } footer: {
            LText("Each cloud account you mount in Finder keeps recently-downloaded files locally up to this size, so re-opening the same file is instant. New value applies to the next mount.")
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

