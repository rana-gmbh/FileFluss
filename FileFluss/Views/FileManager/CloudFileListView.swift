import SwiftUI
import FileFlussCore

struct CloudFileListView: View {
    let panelSide: PanelSide
    let accountId: UUID
    @Environment(AppState.self) private var appState
    @Environment(\.openSettings) private var openSettings
    @AppStorage("showStatusBar") private var showStatusBar = true
    @AppStorage("hideFileExtensions") private var hideFileExtensions = false
    /// Mirrors the Settings → "Confirm before deleting" toggle. When off,
    /// the menu/keyboard Delete action skips the dialog and deletes from
    /// the cloud straight away. Kept in sync with `SettingsView`'s key.
    @AppStorage("confirmDelete") private var confirmDelete = true
    @State private var showDeleteConfirmation = false
    @State private var showConflict = false
    @State private var showDropConfirmation = false
    @State private var pendingUpload: PendingUpload?
    @State private var showCloudToCloudDropConfirmation = false
    @State private var pendingCloudToCloudDrop: PendingCloudToCloudDrop?
    @State private var showInternalCloudDropConfirmation = false
    @State private var pendingInternalCloudDrop: PendingInternalCloudDrop?
    @State private var showNewFolderDialog = false
    @State private var newFolderName = ""
    @State private var showRenameDialog = false
    @State private var renameText = ""
    @State private var renameCloudItem: CloudFileItem?
    @State private var showMountPrompt = false
    @State private var pendingFinderItems: [CloudFileItem] = []
    @State private var mountErrorMessage: String?
    @State private var showMountError = false

    struct PendingUpload {
        let urls: [URL]
        let targetFolder: CloudFileItem?
    }

    struct PendingCloudToCloudDrop {
        let sourceItems: [CloudFileItem]
        let sourceAccountId: UUID
        let targetFolder: CloudFileItem?
    }

    struct PendingInternalCloudDrop {
        let sourceItems: [CloudFileItem]
        let targetFolder: CloudFileItem
    }

    private var vm: CloudFileManagerViewModel {
        appState.cloudFileManager(for: accountId, side: panelSide)
    }

    /// Live-resolved name for the account this panel is showing. Reading
    /// from `syncManager.accounts` each time ensures renames in Settings →
    /// Cloud Accounts are reflected immediately in confirmation dialogs.
    private var accountDisplayName: String {
        appState.syncManager.accounts.first(where: { $0.id == accountId })?.displayName
            ?? "this account"
    }

    private var incomingDirection: ConflictDirection {
        panelSide == .right ? .leftToRight : .rightToLeft
    }

    private var outgoingDirection: ConflictDirection {
        panelSide == .left ? .leftToRight : .rightToLeft
    }

    var body: some View {
        bodyWithDialogs
            .onReceive(NotificationCenter.default.publisher(for: KeyboardCommand.quickFilter.notification)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                vm.isFilterBarVisible = true
            }
            .onReceive(NotificationCenter.default.publisher(for: .menuNewFolder)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                guard appState.canCreateFolderInActivePanel else { return }
                newFolderName = L10n.text("New Folder")
                showNewFolderDialog = true
            }
            .onReceive(NotificationCenter.default.publisher(for: .menuRename)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                if let item = vm.selectedItems.first, vm.selectedItems.count == 1 {
                    renameCloudItem = item
                    renameText = item.name
                    showRenameDialog = true
                }
            }
            .onReceive(NotificationCenter.default.publisher(for: .menuDelete)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                triggerDelete()
            }
            .onReceive(NotificationCenter.default.publisher(for: .menuCopyToOtherPanel)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                let items = vm.selectedItems
                guard !items.isEmpty else { return }
                let otherSide: PanelSide = panelSide == .left ? .right : .left
                vm.conflictDirection = outgoingDirection
                if let otherCloudId = appState.cloudAccountId(for: otherSide) {
                    let otherCloudVM = appState.cloudFileManager(for: otherCloudId, side: otherSide)
                    otherCloudVM.conflictDirection = outgoingDirection
                    let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    transfer.task = Task { await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: false) }
                } else {
                    let otherFM = appState.fileManager(for: otherSide)
                    let transfer = TransferProgress(operation: "Downloading", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    transfer.task = Task {
                        await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                        await otherFM.refresh()
                    }
                }
            }
            .onReceive(NotificationCenter.default.publisher(for: .menuMoveToOtherPanel)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                let items = vm.selectedItems
                guard !items.isEmpty else { return }
                let otherSide: PanelSide = panelSide == .left ? .right : .left
                vm.conflictDirection = outgoingDirection
                if let otherCloudId = appState.cloudAccountId(for: otherSide) {
                    let otherCloudVM = appState.cloudFileManager(for: otherCloudId, side: otherSide)
                    otherCloudVM.conflictDirection = outgoingDirection
                    let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    transfer.task = Task { await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: true) }
                } else {
                    let otherFM = appState.fileManager(for: otherSide)
                    let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    transfer.task = Task {
                        await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                        await vm.deleteItems(items)
                        await otherFM.refresh()
                    }
                }
            }
    }

    private var bodyWithDialogs: some View {
        bodyWithDropDialogs
        .confirmationDialog(L10n.text("Delete from Cloud"), isPresented: $showDeleteConfirmation) {
            // See FileListView's matching dialog for the rationale: without
            // `.defaultAction` here, Return falls through to the global
            // rename shortcut and triggers the rename window as soon as
            // the user dismisses this dialog by other means.
            Button(L10n.text("Delete"), role: .destructive) {
                Task { await vm.deleteSelectedItems() }
            }
            .keyboardShortcut(.defaultAction)
            Button(L10n.text("Cancel"), role: .cancel) {}
        } message: {
            let items = vm.selectedItems
            if items.count == 1 {
                Text(L10n.format("Are you sure you want to delete \"%@\" from %@?", items[0].name, accountDisplayName))
            } else {
                Text(L10n.format("Are you sure you want to delete %d items from %@?", items.count, accountDisplayName))
            }
        }
        .sheet(isPresented: $showConflict) {
            if let conflict = vm.pendingConflict {
                ConflictResolutionView(conflict: conflict) { resolution in
                    showConflict = false
                    vm.resolveConflict(resolution)
                }
            }
        }
        .onChange(of: vm.pendingConflict != nil) { _, hasConflict in
            showConflict = hasConflict
        }
        .alert(L10n.text("New Folder"), isPresented: $showNewFolderDialog) {
            TextField(L10n.text("Folder name"), text: $newFolderName)
            Button(L10n.text("Create")) {
                let name = newFolderName
                Task { await vm.createNewFolder(named: name) }
            }
            Button(L10n.text("Cancel"), role: .cancel) {}
        }
        .alert(L10n.text("Rename"), isPresented: $showRenameDialog) {
            TextField(L10n.text("Name"), text: $renameText)
            Button(L10n.text("Rename")) {
                if let item = renameCloudItem {
                    let newName = renameText
                    Task { await vm.renameItem(item, to: newName) }
                }
            }
            Button(L10n.text("Cancel"), role: .cancel) {}
        }
        .confirmationDialog(
            L10n.format("Do you want to mount “%@” in Finder?", accountDisplayName),
            isPresented: $showMountPrompt,
            titleVisibility: .visible
        ) {
            Button(L10n.text("Mount in Finder")) {
                let items = pendingFinderItems
                pendingFinderItems = []
                mountThenReveal(items)
            }
            .keyboardShortcut(.defaultAction)
            Button(L10n.text("Cancel"), role: .cancel) {
                pendingFinderItems = []
            }
        } message: {
            Text(L10n.text("This account isn't mounted yet. Mounting it adds it as a drive in Finder so you can open this item there."))
        }
        .alert(L10n.text("Could not mount in Finder"), isPresented: $showMountError, presenting: mountErrorMessage) { _ in
            Button(L10n.text("OK"), role: .cancel) {}
        } message: { message in
            Text(message)
        }
    }

    private var bodyWithDropDialogs: some View {
        mainContent
        .task(id: accountId) {
            await vm.loadDirectory()
        }
        .confirmationDialog(L10n.text("Move or Copy?"), isPresented: $showDropConfirmation, presenting: pendingUpload) { upload in
            Button(L10n.text("Copy Here")) {
                pendingUpload = nil
                runUploadDrop(upload, isMove: false)
            }
            Button(L10n.text("Move Here")) {
                pendingUpload = nil
                runUploadDrop(upload, isMove: true)
            }
            Button(L10n.text("Cancel"), role: .cancel) { pendingUpload = nil }
        } message: { upload in
            let providerName = appState.syncManager.accountFor(id: accountId)?.providerType.displayName ?? "Cloud"
            let name = upload.targetFolder?.name
                ?? (vm.currentPath == "/" ? providerName : (vm.currentPath as NSString).lastPathComponent)
            if upload.urls.count == 1 {
                Text(L10n.format("What would you like to do with \"%@\" in \"%@\"?", upload.urls[0].lastPathComponent, name))
            } else {
                Text(L10n.format("What would you like to do with %d items in \"%@\"?", upload.urls.count, name))
            }
        }
        .confirmationDialog(L10n.text("Move or Copy?"), isPresented: $showCloudToCloudDropConfirmation, presenting: pendingCloudToCloudDrop) { drop in
            Button(L10n.text("Copy Here")) {
                pendingCloudToCloudDrop = nil
                runCloudToCloudDrop(drop, isMove: false)
            }
            Button(L10n.text("Move Here")) {
                pendingCloudToCloudDrop = nil
                runCloudToCloudDrop(drop, isMove: true)
            }
            Button(L10n.text("Cancel"), role: .cancel) { pendingCloudToCloudDrop = nil }
        } message: { drop in
            let count = drop.sourceItems.count
            let providerName = appState.syncManager.accountFor(id: accountId)?.providerType.displayName ?? "Cloud"
            let name = drop.targetFolder?.name
                ?? (vm.currentPath == "/" ? providerName : (vm.currentPath as NSString).lastPathComponent)
            Text(count == 1
                 ? L10n.format("What would you like to do with \"%@\" in \"%@\"?", drop.sourceItems[0].name, name)
                 : L10n.format("What would you like to do with %d items in \"%@\"?", count, name))
        }
        .confirmationDialog(L10n.text("Move or Copy?"), isPresented: $showInternalCloudDropConfirmation, presenting: pendingInternalCloudDrop) { drop in
            Button(L10n.text("Copy Here")) {
                pendingInternalCloudDrop = nil
                runInternalCloudDrop(drop, isMove: false)
            }
            Button(L10n.text("Move Here")) {
                pendingInternalCloudDrop = nil
                runInternalCloudDrop(drop, isMove: true)
            }
            Button(L10n.text("Cancel"), role: .cancel) { pendingInternalCloudDrop = nil }
        } message: { drop in
            let count = drop.sourceItems.count
            Text(count == 1
                 ? L10n.format("What would you like to do with \"%@\" in \"%@\"?", drop.sourceItems[0].name, drop.targetFolder.name)
                 : L10n.format("What would you like to do with %d items in \"%@\"?", count, drop.targetFolder.name))
        }
    }

    private var mainContent: some View {
        VStack(spacing: 0) {
            cloudPathBar
            Divider()
            if vm.isFilterBarVisible {
                PanelFilterBar(text: Bindable(vm).searchText, isVisible: Bindable(vm).isFilterBarVisible)
                Divider()
            }
            cloudFileArea
            if showStatusBar {
                Divider()
                cloudStatusFooter
            }
        }
    }

    private var cloudStatusFooter: some View {
        let items = vm.filteredItems
        let fileCount = items.filter { !$0.isDirectory }.count
        let folderCount = items.filter { $0.isDirectory }.count
        let totalSize = items.filter { !$0.isDirectory }.reduce(Int64(0)) { $0 + $1.size }
        let selected = vm.selectedItems

        return HStack(spacing: 4) {
            Text(L10n.format("%d files, %d folders — %@", fileCount, folderCount, ByteCountFormatter.string(fromByteCount: totalSize, countStyle: .file)))
            if !selected.isEmpty {
                Text("·")
                if vm.isCalculatingSelectionSize {
                    Text(L10n.format("Selected: %d items, calculating…", selected.count))
                } else if let size = vm.selectionSize {
                    Text(L10n.format("Selected: %d items, %@", selected.count, ByteCountFormatter.string(fromByteCount: size, countStyle: .file)))
                }
            }
            if let quota = vm.storageQuota {
                Text("·")
                Text(CloudQuotaFormatter.summary(quota, accountDisplayName: accountDisplayName))
            }
            Spacer()
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 4)
        .background(.bar)
        .task(id: accountId) {
            // Initial probe when the panel attaches to a new account.
            vm.storageQuota = await appState.syncManager.storageQuota(for: accountId)
        }
        .onChange(of: vm.items.count) { _, _ in
            // Item count changing means an upload, delete, or folder-
            // create completed and the listing was refreshed. Invalidate
            // the quota cache and re-probe so the bar reflects the new
            // usage without waiting for the 120s TTL.
            Task {
                appState.syncManager.invalidateQuota(for: accountId)
                vm.storageQuota = await appState.syncManager.storageQuota(for: accountId)
            }
        }
    }

    private var cloudPathBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                if let providerType = appState.syncManager.accountFor(id: accountId)?.providerType {
                    CloudProviderIcon(providerType: providerType, size: 12)
                } else {
                    Image(systemName: "cloud.fill")
                        .foregroundStyle(.secondary)
                        .font(.caption)
                }

                ForEach(pathComponents, id: \.path) { component in
                    PathComponentButton(
                        title: component.name,
                        onClick: {
                            Task { await vm.navigateTo(component.path) }
                        },
                        onDropURLs: { urls in
                            handleCloudPathDropURLs(urls, target: component)
                        },
                        onDropCloudPromise: {
                            handleCloudPathDropCloudPromise(target: component)
                        }
                    )

                    if component.path != vm.currentPath {
                        Image(systemName: "chevron.right")
                            .font(.caption2)
                            .foregroundStyle(.tertiary)
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
        }
        .background(.bar)
    }

    /// Build a synthetic `CloudFileItem` for a breadcrumb component. The
    /// existing pending* flows expect a `CloudFileItem?` target; the path
    /// bar only knows (path, name) so we fabricate one with isDirectory=true.
    /// Routes a Delete request through the Settings "Confirm before
    /// deleting" preference: either presents the confirmation dialog or
    /// kicks off the cloud delete straight away. Shared by the menu
    /// (`menuDelete` notification), keyboard, and context-menu paths.
    private func triggerDelete() {
        guard !vm.selectedItems.isEmpty else { return }
        if confirmDelete {
            showDeleteConfirmation = true
        } else {
            Task { await vm.deleteSelectedItems() }
        }
    }

    private func cloudFolder(forPath path: String, name: String) -> CloudFileItem {
        CloudFileItem(
            id: "pathcomp:\(path)",
            name: name,
            path: path,
            isDirectory: true,
            size: 0,
            modificationDate: .distantPast,
            checksum: nil
        )
    }

    private func handleCloudPathDropURLs(_ urls: [URL], target: (name: String, path: String)) -> Bool {
        // Don't trigger when the drop is the cloud panel's own drag
        // (cloud drags don't put fileURLs on the pasteboard, but be safe).
        guard !urls.isEmpty else { return false }
        let folder = cloudFolder(forPath: target.path, name: target.name)
        presentOrRunUploadDrop(PendingUpload(urls: urls, targetFolder: folder))
        return true
    }

    private func handleCloudPathDropCloudPromise(target: (name: String, path: String)) -> Bool {
        guard !appState.cloudDragSourceItems.isEmpty,
              let sourceAccountId = appState.cloudDragSourceAccountId else { return false }
        let folder = cloudFolder(forPath: target.path, name: target.name)

        if sourceAccountId == accountId {
            // Same-account drag: skip when targeting the source folder itself
            // or the directory the items already live in.
            let currentParents = Set(appState.cloudDragSourceItems.map {
                ($0.path as NSString).deletingLastPathComponent
            })
            if currentParents.contains(target.path) { return false }
            if appState.cloudDragSourceItems.contains(where: { $0.path == target.path }) { return false }
            presentOrRunInternalCloudDrop(PendingInternalCloudDrop(
                sourceItems: appState.cloudDragSourceItems,
                targetFolder: folder
            ))
        } else {
            presentOrRunCloudToCloudDrop(PendingCloudToCloudDrop(
                sourceItems: appState.cloudDragSourceItems,
                sourceAccountId: sourceAccountId,
                targetFolder: folder
            ))
        }
        return true
    }

    // MARK: - DragDropMode dispatch

    private func presentOrRunUploadDrop(_ upload: PendingUpload) {
        switch appState.dragDropMode {
        case .copy: runUploadDrop(upload, isMove: false)
        case .move: runUploadDrop(upload, isMove: true)
        case .ask:
            pendingUpload = upload
            showDropConfirmation = true
        }
    }

    private func presentOrRunCloudToCloudDrop(_ drop: PendingCloudToCloudDrop) {
        switch appState.dragDropMode {
        case .copy: runCloudToCloudDrop(drop, isMove: false)
        case .move: runCloudToCloudDrop(drop, isMove: true)
        case .ask:
            pendingCloudToCloudDrop = drop
            showCloudToCloudDropConfirmation = true
        }
    }

    private func presentOrRunInternalCloudDrop(_ drop: PendingInternalCloudDrop) {
        switch appState.dragDropMode {
        case .copy: runInternalCloudDrop(drop, isMove: false)
        case .move: runInternalCloudDrop(drop, isMove: true)
        case .ask:
            pendingInternalCloudDrop = drop
            showInternalCloudDropConfirmation = true
        }
    }

    private func runUploadDrop(_ upload: PendingUpload, isMove: Bool) {
        let urls = upload.urls
        let targetPath = upload.targetFolder?.path
        Task {
            await appState.gateTransfer(
                destAccountId: accountId, destLocalDir: nil,
                localSources: urls, isMove: isMove, verb: isMove ? "Move" : "Copy"
            ) {
                let transfer = TransferProgress(operation: isMove ? "Moving" : "Copying", totalItems: urls.count)
                appState.addTransfer(transfer, panel: panelSide)
                vm.conflictDirection = incomingDirection
                transfer.task = Task {
                    await vm.uploadFiles(from: urls, toPath: targetPath, progress: transfer)
                    if isMove {
                        for url in urls { try? FileManager.default.removeItem(at: url) }
                        let otherSide: PanelSide = panelSide == .left ? .right : .left
                        await appState.fileManager(for: otherSide).refresh()
                    }
                }
            }
        }
    }

    private func runCloudToCloudDrop(_ drop: PendingCloudToCloudDrop, isMove: Bool) {
        let sourceItems = drop.sourceItems
        let sourceAccountId = drop.sourceAccountId
        let targetPath = drop.targetFolder?.path
        // Source side defaults to the *other* panel since cross-account
        // drops are between panels; if a same-account drag was routed
        // here it falls back to the recorded drag side.
        let sourceSide = appState.cloudDragSourceSide
            ?? (panelSide == .left ? .right : .left)
        let sourceVM = appState.cloudFileManager(for: sourceAccountId, side: sourceSide)
        Task {
            await appState.gateTransfer(
                destAccountId: accountId, destLocalDir: nil,
                cloudSources: sourceItems.map { (sourceAccountId, $0) }, isMove: isMove, verb: isMove ? "Move" : "Copy"
            ) {
                sourceVM.conflictDirection = incomingDirection
                vm.conflictDirection = incomingDirection
                let transfer = TransferProgress(operation: isMove ? "Moving" : "Copying", totalItems: sourceItems.count)
                appState.addTransfer(transfer, panel: panelSide)
                transfer.task = Task {
                    await Self.cloudToCloudTransfer(items: sourceItems, from: sourceVM, to: vm, targetSubfolderPath: targetPath, progress: transfer, deleteFromSource: isMove)
                }
            }
        }
    }

    /// Download a cloud file to a temp cache and hand it to the user's
    /// default app via NSWorkspace. Cached copies (same size + mtime) are
    /// reused so reopening a file is instant. A TransferProgress entry
    /// shows feedback while the download runs.
    /// Right-click → Open in Finder. If the account is already mounted as a
    /// WebDAV drive, reveal the item inside that volume. Otherwise ask the
    /// user whether to mount it first, then reveal once the mount succeeds.
    private func openInFinder(_ items: [CloudFileItem]) {
        guard !items.isEmpty else { return }
        if let mount = appState.mountService.mount(for: accountId) {
            revealInMount(items, mount: mount)
        } else {
            pendingFinderItems = items
            showMountPrompt = true
        }
    }

    private func revealInMount(_ items: [CloudFileItem], mount: LoopbackMountService.ActiveMount) {
        let urls = items.map { finderURL(for: $0, mount: mount) }
        guard !urls.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    /// Maps a cloud item's remote path to its location inside the mounted
    /// volume. The mount serves `mount.providerRoot` at its root, so strip
    /// that prefix (and any leading slash) before appending to the mount point.
    private func finderURL(for item: CloudFileItem, mount: LoopbackMountService.ActiveMount) -> URL {
        var relative = item.path
        let root = mount.providerRoot
        if root != "/", relative.hasPrefix(root) {
            relative = String(relative.dropFirst(root.count))
        }
        while relative.hasPrefix("/") { relative.removeFirst() }
        guard !relative.isEmpty else { return mount.mountPoint }
        return mount.mountPoint.appendingPathComponent(relative)
    }

    private func mountThenReveal(_ items: [CloudFileItem]) {
        Task {
            guard let provider = await appState.syncManager.providerFor(accountId: accountId) else {
                mountErrorMessage = L10n.text("The account isn't connected — sign in first.")
                showMountError = true
                return
            }
            do {
                let mount = try await appState.mountService.mount(
                    provider: provider,
                    accountId: accountId,
                    displayName: accountDisplayName
                )
                revealInMount(items, mount: mount)
            } catch {
                mountErrorMessage = error.localizedDescription
                showMountError = true
            }
        }
    }

    private func openCloudFile(_ item: CloudFileItem) {
        let transfer = TransferProgress(operation: "Opening", totalItems: 1)
        transfer.transferredFileNames = [item.name]
        transfer.totalFiles = 1
        appState.addTransfer(transfer, panel: panelSide)
        transfer.task = Task {
            let url = await vm.downloadToTemp(item)
            transfer.completedItems = 1
            transfer.isComplete = true
            transfer.endTime = Date()
            if let url {
                transfer.recordSuccess(item.name)
                NSWorkspace.shared.open(url)
            } else {
                transfer.recordFailure(item.name, error: L10n.text("Could not download file"))
            }
        }
    }

    private func runInternalCloudDrop(_ drop: PendingInternalCloudDrop, isMove: Bool) {
        let items = drop.sourceItems
        let target = drop.targetFolder
        let run: () -> Void = {
            vm.conflictDirection = incomingDirection
            let transfer = TransferProgress(operation: isMove ? "Moving" : "Copying", totalItems: items.count)
            appState.addTransfer(transfer, panel: panelSide)
            transfer.task = Task {
                await vm.transferItemsToSubfolder(items, targetPath: target.path, deleteFromSource: isMove, progress: transfer)
            }
        }
        // A move within the same account just relocates data (net 0) — only a
        // copy duplicates it, so only the copy case needs the space check.
        if isMove { run(); return }
        Task {
            await appState.gateTransfer(
                destAccountId: accountId, destLocalDir: nil,
                cloudSources: items.map { (accountId, $0) }, isMove: false, verb: "Copy",
                proceed: run
            )
        }
    }

    @ViewBuilder
    private var cloudFileArea: some View {
        if vm.isLoading && vm.items.isEmpty {
            ProgressView(L10n.text("Loading..."))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if let error = vm.error {
            cloudErrorBanner(message: error)
        } else {
            NativeCloudFileList(
                items: vm.filteredItems,
                panelSide: panelSide,
                hideFileExtensions: hideFileExtensions,
                selectedIDs: Bindable(vm).selectedItemIDs,
                quickLookController: vm.quickLookController,
                onDoubleClick: { item in
                    if item.isDirectory {
                        Task { await vm.openItem(item) }
                    } else {
                        openCloudFile(item)
                    }
                },
                onDrop: { urls, targetFolder in
                    presentOrRunUploadDrop(PendingUpload(urls: urls, targetFolder: targetFolder))
                },
                onKeySpace: {
                    vm.toggleQuickLook()
                },
                onDelete: {
                    triggerDelete()
                },
                onCopyToOtherPanel: { items in
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    let otherCloudId = appState.cloudAccountId(for: otherSide)
                    let destDir = otherCloudId == nil ? appState.fileManager(for: otherSide).currentDirectory : nil
                    Task {
                        await appState.gateTransfer(
                            destAccountId: otherCloudId, destLocalDir: destDir,
                            cloudSources: items.map { (accountId, $0) }, isMove: false, verb: "Copy"
                        ) {
                            vm.conflictDirection = outgoingDirection
                            if let otherCloudId {
                                let otherCloudVM = appState.cloudFileManager(for: otherCloudId, side: otherSide)
                                otherCloudVM.conflictDirection = outgoingDirection
                                let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
                                appState.addTransfer(transfer, panel: otherSide)
                                transfer.task = Task {
                                    await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: false)
                                }
                            } else {
                                let otherFM = appState.fileManager(for: otherSide)
                                let transfer = TransferProgress(operation: "Downloading", totalItems: items.count)
                                appState.addTransfer(transfer, panel: otherSide)
                                transfer.task = Task {
                                    await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                                    await otherFM.refresh()
                                }
                            }
                        }
                    }
                },
                onMoveToOtherPanel: { items in
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    let otherCloudId = appState.cloudAccountId(for: otherSide)
                    let destDir = otherCloudId == nil ? appState.fileManager(for: otherSide).currentDirectory : nil
                    Task {
                        await appState.gateTransfer(
                            destAccountId: otherCloudId, destLocalDir: destDir,
                            cloudSources: items.map { (accountId, $0) }, isMove: true, verb: "Move"
                        ) {
                            vm.conflictDirection = outgoingDirection
                            if let otherCloudId {
                                let otherCloudVM = appState.cloudFileManager(for: otherCloudId, side: otherSide)
                                otherCloudVM.conflictDirection = outgoingDirection
                                let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                                appState.addTransfer(transfer, panel: otherSide)
                                transfer.task = Task {
                                    await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: true)
                                }
                            } else {
                                let otherFM = appState.fileManager(for: otherSide)
                                let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                                appState.addTransfer(transfer, panel: otherSide)
                                transfer.task = Task {
                                    await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                                    await vm.deleteItems(items)
                                    await otherFM.refresh()
                                }
                            }
                        }
                    }
                },
                onCalculateFolderSize: { folder in
                    appState.calculateCloudFolderSize(
                        path: folder.path,
                        name: folder.name,
                        accountId: accountId,
                        panel: panelSide
                    )
                },
                onAddToFavorites: { folder in
                    appState.addCloudFavorite(
                        accountId: accountId,
                        path: folder.path,
                        name: folder.name,
                        to: panelSide
                    )
                },
                onSortChanged: { key, ascending in
                    switch key {
                    case "name": vm.sortOrder = .name
                    case "date": vm.sortOrder = .date
                    case "size": vm.sortOrder = .size
                    case "kind": vm.sortOrder = .kind
                    default: break
                    }
                    vm.sortAscending = ascending
                },
                onBecameActive: {
                    appState.activePanel = panelSide
                },
                onDownloadToTemp: { item, completion in
                    Task {
                        let url = await vm.downloadToTemp(item)
                        completion(url)
                    }
                },
                onDragSessionStarted: { draggedItems in
                    appState.cloudDragSourceItems = draggedItems
                    appState.cloudDragSourceAccountId = accountId
                    appState.cloudDragSourceSide = panelSide
                },
                onDragSessionEnded: {
                    appState.cloudDragSourceItems = []
                    appState.cloudDragSourceAccountId = nil
                    appState.cloudDragSourceSide = nil
                },
                onReceiveCloudDrop: { targetFolder in
                    guard !appState.cloudDragSourceItems.isEmpty,
                          let sourceAccountId = appState.cloudDragSourceAccountId else { return }
                    let sourceItems = appState.cloudDragSourceItems

                    if sourceAccountId == accountId {
                        // Same cloud account, but the drag landed on a
                        // *different* panel's table view (per-panel VMs).
                        // Resolve the target folder (or fall back to this
                        // panel's current directory) and route through the
                        // in-cloud move/copy path so we don't re-upload.
                        let resolvedTarget: CloudFileItem = targetFolder ?? {
                            let path = vm.currentPath
                            let name = path == "/" ? "/" : (path as NSString).lastPathComponent
                            return cloudFolder(forPath: path, name: name)
                        }()

                        // No-op when the drop wouldn't move anything.
                        let currentParents = Set(sourceItems.map { ($0.path as NSString).deletingLastPathComponent })
                        if currentParents.contains(resolvedTarget.path) { return }
                        if sourceItems.contains(where: { $0.path == resolvedTarget.path }) { return }

                        presentOrRunInternalCloudDrop(PendingInternalCloudDrop(
                            sourceItems: sourceItems,
                            targetFolder: resolvedTarget
                        ))
                    } else {
                        presentOrRunCloudToCloudDrop(PendingCloudToCloudDrop(
                            sourceItems: sourceItems,
                            sourceAccountId: sourceAccountId,
                            targetFolder: targetFolder
                        ))
                    }
                },
                onInternalCloudDrop: { items, target in
                    presentOrRunInternalCloudDrop(PendingInternalCloudDrop(
                        sourceItems: items,
                        targetFolder: target
                    ))
                },
                onCreateFolder: {
                    newFolderName = L10n.text("New Folder")
                    showNewFolderDialog = true
                },
                onRename: { item in
                    renameCloudItem = item
                    renameText = item.name
                    showRenameDialog = true
                },
                onOpenInFinder: { items in
                    openInFinder(items)
                },
                canCreateFolder: appState.syncManager.accountFor(id: accountId)?.providerType != .wordpress
            )
            .onChange(of: vm.selectedItemIDs) {
                vm.updateQuickLookSelection()
                vm.recalculateSelectionSize()
            }
            .overlay {
                if vm.filteredItems.isEmpty && !vm.isLoading {
                    ContentUnavailableView(L10n.text("Empty Folder"), systemImage: "folder", description: Text(L10n.text("This cloud folder is empty")))
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                        .padding(.top, 40)
                        .allowsHitTesting(false)
                }
                if vm.isLoading {
                    ProgressView()
                        .scaleEffect(0.5)
                        .padding(4)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                }
            }
        }
    }

    /// Download items from source cloud to a temp directory, then upload to target cloud.
    /// Conflicts are resolved BEFORE downloading to avoid wasting bandwidth.
    private static func cloudToCloudTransfer(
        items: [CloudFileItem],
        from sourceVM: CloudFileManagerViewModel,
        to targetVM: CloudFileManagerViewModel,
        targetSubfolderPath: String? = nil,
        progress: TransferProgress?,
        deleteFromSource: Bool
    ) async {
        // Phase 0: Pre-flight conflict resolution (no downloads yet)
        // If a subfolder target is specified, list that folder for conflict detection.
        let targetExistingItems: [CloudFileItem]?
        if let targetSubfolderPath {
            let provider = await SyncEngine.shared.provider(for: targetVM.accountId)
            targetExistingItems = (try? await provider?.listDirectory(at: targetSubfolderPath)) ?? []
        } else {
            targetExistingItems = nil
        }
        let resolutions = await targetVM.preFlightConflictCheck(sourceItems: items, against: targetExistingItems)

        let itemsToTransfer = resolutions.filter { $0.1 != .skip }.map(\.0)
        guard !itemsToTransfer.isEmpty else {
            progress?.endTime = Date()
            progress?.isComplete = true
            return
        }

        // Build lookup for resolution per item name
        let resolutionByName = Dictionary(resolutions.map { ($0.0.name, $0.1) }, uniquingKeysWith: { _, last in last })

        let tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("cloud-transfer-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)

        defer {
            try? FileManager.default.removeItem(at: tempDir)
        }

        // Pre-compute expected bytes across both phases for byte-weighted progress.
        // Source bytes = upload bytes (since we upload what we downloaded).
        let sourceProvider = await SyncEngine.shared.provider(for: sourceVM.accountId)
        var expectedBytes: Int64 = 0
        if let sourceProvider {
            for item in itemsToTransfer {
                if item.isDirectory {
                    expectedBytes += (try? await sourceProvider.folderSize(at: item.path)) ?? 0
                } else {
                    expectedBytes += item.size
                }
            }
        }

        // Phase 1: Download only non-skipped items
        if let progress {
            progress.isCloudToCloud = true
            progress.currentPhase = .downloading
            progress.downloadStartTime = Date()
            progress.expectedBytesDownload = expectedBytes
            progress.expectedBytesUpload = expectedBytes
        }

        await sourceVM.downloadItems(itemsToTransfer, to: tempDir, progress: progress, skipConflictCheck: true)

        if let progress {
            progress.downloadEndTime = Date()
            // Many providers fall back to the non-streaming default of
            // `downloadFile`, which never calls `onBytes`. Without this
            // top-up `downloadBytes` stays at 0 and the dual-phase
            // fraction pegs at 50% after upload — visible as "Done at 50%".
            if progress.downloadBytes < expectedBytes {
                progress.addDownloadBytes(expectedBytes - progress.downloadBytes)
            }
        }

        // Phase 2: Delete items that need replacing, then upload
        let provider = await SyncEngine.shared.provider(for: targetVM.accountId)
        let conflictPool = targetExistingItems ?? targetVM.items
        for item in itemsToTransfer {
            if resolutionByName[item.name] == .replace {
                // Find and delete existing item on target
                if let existing = conflictPool.first(where: { $0.name == item.name }) {
                    try? await provider?.deleteItem(at: existing.path)
                }
            }
        }

        // Build local URLs for upload, renaming "keepBoth" items
        let existingNames = Set(conflictPool.map(\.name))
        let localURLs: [URL] = itemsToTransfer.compactMap { item -> URL? in
            let expected = tempDir.appendingPathComponent(item.name)
            var localURL: URL?
            if FileManager.default.fileExists(atPath: expected.path) {
                localURL = expected
            } else {
                for ext in ["docx", "xlsx", "pptx", "pdf"] {
                    let converted = expected.appendingPathExtension(ext)
                    if FileManager.default.fileExists(atPath: converted.path) {
                        localURL = converted
                        break
                    }
                }
            }
            guard let url = localURL else { return nil }

            // For keepBoth, rename the local temp file to a unique name
            if resolutionByName[item.name] == .keepBoth {
                let uniqueName = CloudFileManagerViewModel.uniqueCloudName(for: url.lastPathComponent, existing: existingNames)
                let renamedURL = tempDir.appendingPathComponent(uniqueName)
                do {
                    try FileManager.default.moveItem(at: url, to: renamedURL)
                    return renamedURL
                } catch {
                    return url
                }
            }
            return url
        }

        if let progress {
            progress.currentPhase = .uploading
            progress.completedItems = 0
            progress.currentFileName = ""
            progress.uploadStartTime = Date()
        }

        // Upload with conflict check skipped — we already handled conflicts
        await targetVM.uploadFiles(from: localURLs, toPath: targetSubfolderPath, progress: progress, skipConflictCheck: true)

        if let progress {
            progress.uploadEndTime = Date()
            // Mirror the download top-up: providers without streaming
            // uploads would leave uploadBytes at 0.
            if progress.uploadBytes < expectedBytes {
                progress.addUploadBytes(expectedBytes - progress.uploadBytes)
            }
            progress.totalBytes = progress.downloadBytes + progress.uploadBytes
        }

        if deleteFromSource {
            await sourceVM.deleteItems(itemsToTransfer)
            await sourceVM.loadDirectory()
        }

        if let progress {
            progress.endTime = Date()
            progress.isComplete = true
        }
    }

    /// Top-aligned error banner shown in place of the file list when a
    /// cloud listing fails. When the failure is an auth problem and the
    /// provider supports inline OAuth re-auth, surfaces a "Sign In Again"
    /// button; otherwise routes to Settings → Cloud Accounts.
    @ViewBuilder
    private func cloudErrorBanner(message: String) -> some View {
        let providerType = appState.syncManager.accountFor(id: accountId)?.providerType
        let canReauthInline = providerType.map { appState.syncManager.providerSupportsInlineReauth($0) } ?? false
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle")
                .font(.system(size: 36))
                .foregroundStyle(.secondary)
            Text(vm.needsReAuth ? L10n.text("Sign-In Required") : L10n.text("Error"))
                .font(.headline)
            Text(message)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            if vm.needsReAuth {
                HStack(spacing: 8) {
                    if canReauthInline {
                        Button(L10n.text("Sign In Again")) {
                            Task {
                                await appState.syncManager.reauthenticate(accountId: accountId)
                                await vm.loadDirectory()
                            }
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.regular)
                    }
                    Button(canReauthInline ? L10n.text("Open Settings…") : L10n.text("Open Settings to Re-Connect…")) {
                        try? openSettings()
                    }
                    .controlSize(.regular)
                }
                .padding(.top, 4)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .padding(.top, 40)
        .padding(.horizontal, 24)
    }

    private var pathComponents: [(name: String, path: String)] {
        let rootName = appState.syncManager.accountFor(id: accountId)?.providerType.displayName ?? "Cloud"
        var components: [(String, String)] = [(rootName, "/")]
        let parts = vm.currentPath.split(separator: "/", omittingEmptySubsequences: true)
        var accumulated = ""
        for part in parts {
            accumulated += "/\(part)"
            components.append((String(part), accumulated))
        }
        return components
    }
}
