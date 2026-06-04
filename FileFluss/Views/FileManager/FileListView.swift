import SwiftUI
import FileFlussCore

struct FileListView: View {
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState
    @AppStorage("showStatusBar") private var showStatusBar = true
    @AppStorage("hideFileExtensions") private var hideFileExtensions = false
    /// Mirrors the Settings → "Confirm before deleting" toggle. When off,
    /// the menu/keyboard Delete action skips the dialog and moves to Trash
    /// straight away. Kept in sync with `SettingsView`'s @AppStorage key.
    @AppStorage("confirmDelete") private var confirmDelete = true
    @State private var showDropConfirmation: Bool = false
    @State private var showDeleteConfirmation: Bool = false
    @State private var showConflict: Bool = false
    @State private var showCloudDropConfirmation: Bool = false
    @State private var pendingCloudDrop: PendingCloudDrop?
    @State private var showNewFolderDialog: Bool = false
    @State private var newFolderName: String = ""
    @State private var showRenameDialog: Bool = false
    @State private var renameText: String = ""
    @State private var renameItem: FileItem?

    struct PendingCloudDrop {
        let sourceItems: [CloudFileItem]
        let sourceAccountId: UUID
        let targetDirectory: URL
    }

    private var fm: FileManagerViewModel {
        appState.fileManager(for: panelSide)
    }

    /// Direction for conflict dialog: source panel → destination panel (this panel receives)
    private var incomingDirection: ConflictDirection {
        panelSide == .right ? .leftToRight : .rightToLeft
    }

    /// Direction for conflict dialog: this panel → other panel (this panel sends)
    private var outgoingDirection: ConflictDirection {
        panelSide == .left ? .leftToRight : .rightToLeft
    }


    var body: some View {
        VStack(spacing: 0) {
            pathBar
            Divider()
            if fm.isFilterBarVisible {
                PanelFilterBar(text: Bindable(fm).searchText, isVisible: Bindable(fm).isFilterBarVisible)
                Divider()
            }
            fileArea
            if showStatusBar {
                Divider()
                statusFooter
            }
        }
        .confirmationDialog(
            L10n.text("Move or Copy?"),
            isPresented: $showDropConfirmation,
            presenting: fm.pendingDrop
        ) { drop in
            Button(L10n.text("Copy Here")) { runLocalDrop(drop, isMove: false) }
            Button(L10n.text("Move Here")) { runLocalDrop(drop, isMove: true) }
            Button(L10n.text("Cancel"), role: .cancel) {
                fm.pendingDrop = nil
            }
        } message: { drop in
            let count = drop.items.count
            let name = drop.destinationFolder.lastPathComponent
            Text(count == 1
                 ? L10n.format("What would you like to do with \"%@\" in \"%@\"?", drop.items[0].name, name)
                 : L10n.format("What would you like to do with %d items in \"%@\"?", count, name))
        }
        .confirmationDialog(
            L10n.text("Move to Trash"),
            isPresented: $showDeleteConfirmation
        ) {
            // `.defaultAction` matters here for two reasons. First, it
            // matches Finder's own Move-to-Trash dialog where Return
            // confirms. Second — and more importantly — without it the
            // Return keypress isn't consumed by the dialog, so it
            // propagates to the global menu-shortcut dispatcher and fires
            // `KeyboardCommand.rename` (also bound to Return), which
            // queues a rename and pops the rename window up the moment
            // the user dismisses the delete dialog with a mouse click.
            Button(L10n.text("Move to Trash"), role: .destructive) {
                Task {
                    await fm.trashSelectedItems()
                    await appState.refreshAllPanels()
                }
            }
            .keyboardShortcut(.defaultAction)
            Button(L10n.text("Cancel"), role: .cancel) {}
        } message: {
            let items = fm.selectedItems
            if items.count == 1 {
                Text(L10n.format("Are you sure you want to move \"%@\" to the Trash?", items[0].name))
            } else {
                Text(L10n.format("Are you sure you want to move %d items to the Trash?", items.count))
            }
        }
        .sheet(isPresented: $showConflict) {
            if let conflict = fm.pendingConflict {
                ConflictResolutionView(conflict: conflict) { resolution in
                    showConflict = false
                    fm.resolveConflict(resolution)
                }
            }
        }
        .onChange(of: fm.pendingConflict != nil) { _, hasConflict in
            showConflict = hasConflict
        }
        .confirmationDialog(
            L10n.text("Move or Copy?"),
            isPresented: $showCloudDropConfirmation,
            presenting: pendingCloudDrop
        ) { drop in
            Button(L10n.text("Copy Here")) {
                pendingCloudDrop = nil
                runCloudToLocalDrop(drop, isMove: false)
            }
            Button(L10n.text("Move Here")) {
                pendingCloudDrop = nil
                runCloudToLocalDrop(drop, isMove: true)
            }
            Button(L10n.text("Cancel"), role: .cancel) {
                pendingCloudDrop = nil
            }
        } message: { drop in
            let count = drop.sourceItems.count
            let name = drop.targetDirectory.lastPathComponent
            Text(count == 1
                 ? L10n.format("What would you like to do with \"%@\" in \"%@\"?", drop.sourceItems[0].name, name)
                 : L10n.format("What would you like to do with %d items in \"%@\"?", count, name))
        }
        .alert(L10n.text("New Folder"), isPresented: $showNewFolderDialog) {
            TextField(L10n.text("Folder name"), text: $newFolderName)
            Button(L10n.text("Create")) {
                let name = newFolderName
                Task { await fm.createNewFolder(named: name) }
            }
            Button(L10n.text("Cancel"), role: .cancel) {}
        }
        .sheet(isPresented: $showRenameDialog) {
            RenameSheet(
                text: $renameText,
                onRename: {
                    if let item = renameItem {
                        let newName = renameText
                        Task { await fm.renameItem(item, to: newName) }
                    }
                    showRenameDialog = false
                },
                onCancel: { showRenameDialog = false }
            )
        }
        .onReceive(NotificationCenter.default.publisher(for: KeyboardCommand.quickFilter.notification)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            fm.isFilterBarVisible = true
        }
        .onReceive(NotificationCenter.default.publisher(for: .menuNewFolder)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            newFolderName = L10n.text("New Folder")
            showNewFolderDialog = true
        }
        .onReceive(NotificationCenter.default.publisher(for: .menuRename)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            if let item = fm.selectedItems.first, fm.selectedItems.count == 1 {
                renameItem = item
                renameText = item.name
                showRenameDialog = true
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .menuDelete)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            triggerDelete()
        }
        .onReceive(NotificationCenter.default.publisher(for: .menuCopyToOtherPanel)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            let items = fm.selectedItems
            guard !items.isEmpty else { return }
            let otherSide: PanelSide = panelSide == .left ? .right : .left
            let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
            appState.addTransfer(transfer, panel: otherSide)
            if let cloudId = appState.cloudAccountId(for: otherSide) {
                let cloudVM = appState.cloudFileManager(for: cloudId, side: otherSide)
                cloudVM.conflictDirection = outgoingDirection
                transfer.task = Task { await cloudVM.uploadFiles(from: items.map(\.url), progress: transfer) }
            } else {
                let otherFM = appState.fileManager(for: otherSide)
                fm.conflictDirection = outgoingDirection
                transfer.task = Task {
                    await fm.performCopy(items: items, to: otherFM.currentDirectory, progress: transfer)
                    await appState.refreshAllPanels()
                }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .menuMoveToOtherPanel)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            let items = fm.selectedItems
            guard !items.isEmpty else { return }
            let otherSide: PanelSide = panelSide == .left ? .right : .left
            let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
            appState.addTransfer(transfer, panel: otherSide)
            if let cloudId = appState.cloudAccountId(for: otherSide) {
                let cloudVM = appState.cloudFileManager(for: cloudId, side: otherSide)
                cloudVM.conflictDirection = outgoingDirection
                transfer.task = Task {
                    await cloudVM.uploadFiles(from: items.map(\.url), progress: transfer)
                    await fm.deleteItems(items)
                    await fm.refresh()
                }
            } else {
                let otherFM = appState.fileManager(for: otherSide)
                fm.conflictDirection = outgoingDirection
                transfer.task = Task {
                    await fm.performMove(items: items, to: otherFM.currentDirectory, progress: transfer)
                    await appState.refreshAllPanels()
                }
            }
        }
    }

    private var pathBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                ForEach(pathComponents, id: \.url) { component in
                    PathComponentButton(
                        title: component.name,
                        onClick: {
                            Task { await fm.navigateTo(component.url) }
                        },
                        onDropURLs: { urls in
                            handlePathDropURLs(urls, target: component.url)
                        },
                        onDropCloudPromise: {
                            handlePathDropCloudPromise(target: component.url)
                        }
                    )

                    if component.url != fm.currentDirectory {
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

    /// Map a drop of file URLs onto a breadcrumb component into the existing
    /// "Move or Copy?" confirmation flow. Resolves URLs against the current
    /// items first (covers same-panel drags) and falls back to building
    /// `FileItem`s from disk for Finder/cross-panel sources.
    /// Either presents the Move-to-Trash dialog or fires the trash action
    /// straight away, depending on the Settings "Confirm before deleting"
    /// preference. Used by both the menu (`menuDelete` notification) and
    /// the keyboard / context-menu Delete callback so the preference takes
    /// effect everywhere a delete can be initiated from this view.
    private func triggerDelete() {
        guard !fm.selectedItems.isEmpty else { return }
        if confirmDelete {
            showDeleteConfirmation = true
        } else {
            Task {
                await fm.trashSelectedItems()
                await appState.refreshAllPanels()
            }
        }
    }

    private func handlePathDropURLs(_ urls: [URL], target: URL) -> Bool {
        let urlPaths = Set(urls.map { $0.standardizedFileURL.path() })
        let matched = fm.items.filter { urlPaths.contains($0.url.standardizedFileURL.path()) }
        let droppedItems: [FileItem]
        if !matched.isEmpty {
            droppedItems = matched
        } else {
            droppedItems = urls.compactMap { url -> FileItem? in
                guard FileManager.default.fileExists(atPath: url.path) else { return nil }
                return FileItem(url: url)
            }
        }
        guard !droppedItems.isEmpty else { return false }

        // Don't drop a folder onto itself or into the directory it already lives in.
        if droppedItems.contains(where: { $0.url.standardizedFileURL == target.standardizedFileURL }) {
            return false
        }
        let allInSameDir = droppedItems.allSatisfy {
            $0.url.deletingLastPathComponent().standardizedFileURL == target.standardizedFileURL
        }
        guard !allInSameDir else { return false }

        let drop = FileManagerViewModel.PendingDrop(items: droppedItems, destinationFolder: target)
        presentOrRunLocalDrop(drop)
        return true
    }

    /// Drop a cloud-panel drag onto a breadcrumb component. Reuses
    /// `cloudDragSource*` state set by the cloud panel's drag-start hook.
    private func handlePathDropCloudPromise(target: URL) -> Bool {
        guard !appState.cloudDragSourceItems.isEmpty,
              let sourceAccountId = appState.cloudDragSourceAccountId else { return false }
        let drop = PendingCloudDrop(
            sourceItems: appState.cloudDragSourceItems,
            sourceAccountId: sourceAccountId,
            targetDirectory: target
        )
        presentOrRunCloudToLocalDrop(drop)
        return true
    }

    /// Decides whether to show the Move/Copy dialog or run the operation
    /// directly based on `appState.dragDropMode`.
    private func presentOrRunLocalDrop(_ drop: FileManagerViewModel.PendingDrop) {
        fm.pendingDrop = drop
        switch appState.dragDropMode {
        case .copy:
            runLocalDrop(drop, isMove: false)
            fm.pendingDrop = nil
        case .move:
            runLocalDrop(drop, isMove: true)
            fm.pendingDrop = nil
        case .ask:
            showDropConfirmation = true
        }
    }

    private func presentOrRunCloudToLocalDrop(_ drop: PendingCloudDrop) {
        switch appState.dragDropMode {
        case .copy:
            runCloudToLocalDrop(drop, isMove: false)
        case .move:
            runCloudToLocalDrop(drop, isMove: true)
        case .ask:
            pendingCloudDrop = drop
            showCloudDropConfirmation = true
        }
    }

    private func runLocalDrop(_ drop: FileManagerViewModel.PendingDrop, isMove: Bool) {
        let items = drop.items
        let dest = drop.destinationFolder
        Task {
            await appState.gateTransfer(
                destAccountId: nil, destLocalDir: dest,
                localSources: items.map(\.url), isMove: isMove, verb: isMove ? "Move" : "Copy"
            ) {
                let transfer = TransferProgress(operation: isMove ? "Moving" : "Copying", totalItems: items.count)
                appState.addTransfer(transfer, panel: panelSide)
                fm.conflictDirection = incomingDirection
                transfer.task = Task {
                    if isMove {
                        await fm.performMove(items: items, to: dest, progress: transfer)
                    } else {
                        await fm.performCopy(items: items, to: dest, progress: transfer)
                    }
                    await appState.refreshAllPanels()
                }
            }
        }
    }

    private func runCloudToLocalDrop(_ drop: PendingCloudDrop, isMove: Bool) {
        let sourceItems = drop.sourceItems
        let targetDir = drop.targetDirectory
        let sourceAccountId = drop.sourceAccountId
        // The cloud drag must originate from the *other* panel (we're a
        // local panel). Fall back to the recorded drag side just in case.
        let sourceSide = appState.cloudDragSourceSide
            ?? (panelSide == .left ? .right : .left)
        Task {
            await appState.gateTransfer(
                destAccountId: nil, destLocalDir: targetDir,
                cloudSources: sourceItems.map { (sourceAccountId, $0) }, isMove: isMove, verb: isMove ? "Move" : "Copy"
            ) {
                let transfer = TransferProgress(operation: isMove ? "Moving" : "Copying", totalItems: sourceItems.count)
                appState.addTransfer(transfer, panel: panelSide)
                transfer.task = Task {
                    let cloudVM = appState.cloudFileManager(for: sourceAccountId, side: sourceSide)
                    cloudVM.conflictDirection = incomingDirection
                    await cloudVM.downloadItems(sourceItems, to: targetDir, progress: transfer)
                    if isMove {
                        await cloudVM.deleteItems(sourceItems)
                    }
                    await fm.refresh()
                }
            }
        }
    }

    private var fileArea: some View {
        Group {
            if fm.isLoading {
                ProgressView()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                NativeFileList(
                    items: fm.filteredItems,
                    currentDirectory: fm.currentDirectory,
                    panelSide: panelSide,
                    hideFileExtensions: hideFileExtensions,
                    selectedIDs: Bindable(fm).selectedItemIDs,
                    quickLookController: fm.quickLookController,
                    onDoubleClick: { item in
                        Task {
                            await fm.openItem(item)
                            // Entering a folder reloads the list, which drops
                            // first responder; ask for it back so the arrow
                            // keys keep working without re-clicking the pane.
                            if item.isDirectory { appState.requestActivePanelFocus() }
                        }
                    },
                    onDrop: { droppedItems, targetURL in
                        let drop = FileManagerViewModel.PendingDrop(
                            items: droppedItems,
                            destinationFolder: targetURL
                        )
                        presentOrRunLocalDrop(drop)
                    },
                    onKeySpace: {
                        fm.toggleQuickLook()
                    },
                    onDelete: {
                        triggerDelete()
                    },
                    onCopyToOtherPanel: { items in
                        let otherSide: PanelSide = panelSide == .left ? .right : .left
                        let cloudId = appState.cloudAccountId(for: otherSide)
                        let destDir = cloudId == nil ? appState.fileManager(for: otherSide).currentDirectory : nil
                        Task {
                            await appState.gateTransfer(
                                destAccountId: cloudId, destLocalDir: destDir,
                                localSources: items.map(\.url), isMove: false, verb: "Copy"
                            ) {
                                let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
                                appState.addTransfer(transfer, panel: otherSide)
                                fm.conflictDirection = outgoingDirection
                                if let cloudId {
                                    let cloudVM = appState.cloudFileManager(for: cloudId, side: otherSide)
                                    cloudVM.conflictDirection = outgoingDirection
                                    transfer.task = Task {
                                        await cloudVM.uploadFiles(from: items.map(\.url), progress: transfer)
                                    }
                                } else {
                                    let otherFM = appState.fileManager(for: otherSide)
                                    transfer.task = Task {
                                        await fm.performCopy(items: items, to: otherFM.currentDirectory, progress: transfer)
                                        await appState.refreshAllPanels()
                                    }
                                }
                            }
                        }
                    },
                    onMoveToOtherPanel: { items in
                        let otherSide: PanelSide = panelSide == .left ? .right : .left
                        let cloudId = appState.cloudAccountId(for: otherSide)
                        let destDir = cloudId == nil ? appState.fileManager(for: otherSide).currentDirectory : nil
                        Task {
                            await appState.gateTransfer(
                                destAccountId: cloudId, destLocalDir: destDir,
                                localSources: items.map(\.url), isMove: true, verb: "Move"
                            ) {
                                let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                                appState.addTransfer(transfer, panel: otherSide)
                                fm.conflictDirection = outgoingDirection
                                if let cloudId {
                                    let cloudVM = appState.cloudFileManager(for: cloudId, side: otherSide)
                                    cloudVM.conflictDirection = outgoingDirection
                                    transfer.task = Task {
                                        await cloudVM.uploadFiles(from: items.map(\.url), progress: transfer)
                                        await fm.deleteItems(items)
                                        await fm.refresh()
                                    }
                                } else {
                                    let otherFM = appState.fileManager(for: otherSide)
                                    transfer.task = Task {
                                        await fm.performMove(items: items, to: otherFM.currentDirectory, progress: transfer)
                                        await appState.refreshAllPanels()
                                    }
                                }
                            }
                        }
                    },
                    onCalculateFolderSize: { folder in
                        appState.calculateFolderSize(for: folder.url, panel: panelSide)
                    },
                    onAddToFavorites: { folder in
                        appState.addLocalFavorite(url: folder.url, to: panelSide)
                    },
                    onSortChanged: { key, ascending in
                        switch key {
                        case "name": fm.sortOrder = .name
                        case "date": fm.sortOrder = .date
                        case "dateCreated": fm.sortOrder = .dateCreated
                        case "size": fm.sortOrder = .size
                        case "kind": fm.sortOrder = .kind
                        default: break
                        }
                        fm.sortAscending = ascending
                    },
                    onBecameActive: {
                        appState.activePanel = panelSide
                    },
                    onReceivePromises: { targetDir in
                        if !appState.cloudDragSourceItems.isEmpty,
                           let sourceAccountId = appState.cloudDragSourceAccountId {
                            let drop = PendingCloudDrop(
                                sourceItems: appState.cloudDragSourceItems,
                                sourceAccountId: sourceAccountId,
                                targetDirectory: targetDir
                            )
                            presentOrRunCloudToLocalDrop(drop)
                        }
                        Task { await fm.refresh() }
                    },
                    onCreateFolder: {
                        newFolderName = L10n.text("New Folder")
                        showNewFolderDialog = true
                    },
                    onRename: { item in
                        renameItem = item
                        renameText = item.name
                        showRenameDialog = true
                    },
                    onOpenInFinder: { items in
                        // Local files always have a real Finder path — reveal directly.
                        let urls = items.map(\.url)
                        guard !urls.isEmpty else { return }
                        NSWorkspace.shared.activateFileViewerSelecting(urls)
                    },
                    focusToken: appState.focusRequestPanel == panelSide ? appState.focusRequestToken : nil
                )
                .onChange(of: fm.selectedItemIDs) {
                    fm.updateQuickLookSelection()
                    fm.recalculateSelectionSize()
                }
                .overlay {
                    if fm.filteredItems.isEmpty {
                        ContentUnavailableView(L10n.text("No Files"), systemImage: "folder", description: Text(L10n.text("This folder is empty")))
                            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                            .padding(.top, 40)
                            .allowsHitTesting(false)
                    }
                }
            }
        }
    }

    private var statusFooter: some View {
        let items = fm.filteredItems
        let fileCount = items.filter { !$0.isDirectory }.count
        let folderCount = items.filter { $0.isDirectory }.count
        let totalSize = items.filter { !$0.isDirectory }.reduce(Int64(0)) { $0 + $1.size }
        let selected = fm.selectedItems

        return HStack(spacing: 4) {
            Text(L10n.format("%d files, %d folders — %@", fileCount, folderCount, ByteCountFormatter.string(fromByteCount: totalSize, countStyle: .file)))
            if !selected.isEmpty {
                Text("·")
                if fm.isCalculatingSelectionSize {
                    Text(L10n.format("Selected: %d items, calculating…", selected.count))
                } else if let size = fm.selectionSize {
                    Text(L10n.format("Selected: %d items, %@", selected.count, ByteCountFormatter.string(fromByteCount: size, countStyle: .file)))
                }
            }
            Spacer()
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 4)
        .background(.bar)
    }

    private var pathComponents: [(name: String, url: URL)] {
        var components: [(String, URL)] = []
        // Standardize so a stray `/..` collapses to `/` instead of walking an
        // endless `/../..` chain. The fixed-point check is a belt-and-braces
        // guard against any URL whose parent never reduces to root.
        var url = fm.currentDirectory.standardizedFileURL
        while url.path() != "/" {
            let parent = url.deletingLastPathComponent().standardizedFileURL
            if parent.path() == url.path() { break }
            components.insert((url.lastPathComponent, url), at: 0)
            url = parent
        }
        components.insert(("/", URL(filePath: "/")), at: 0)
        return components
    }
}

