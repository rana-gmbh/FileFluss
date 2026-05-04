import SwiftUI

struct FileListView: View {
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState
    @AppStorage("showStatusBar") private var showStatusBar = true
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
            fileArea
            if showStatusBar {
                Divider()
                statusFooter
            }
        }
        .confirmationDialog(
            "Move or Copy?",
            isPresented: $showDropConfirmation,
            presenting: fm.pendingDrop
        ) { drop in
            Button("Copy Here") {
                let items = drop.items
                let dest = drop.destinationFolder
                let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
                appState.addTransfer(transfer, panel: panelSide)
                fm.conflictDirection = incomingDirection
                transfer.task = Task {
                    await fm.performCopy(items: items, to: dest, progress: transfer)
                    await appState.refreshAllPanels()
                }
            }
            Button("Move Here") {
                let items = drop.items
                let dest = drop.destinationFolder
                let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                appState.addTransfer(transfer, panel: panelSide)
                fm.conflictDirection = incomingDirection
                transfer.task = Task {
                    await fm.performMove(items: items, to: dest, progress: transfer)
                    await appState.refreshAllPanels()
                }
            }
            Button("Cancel", role: .cancel) {
                fm.pendingDrop = nil
            }
        } message: { drop in
            let count = drop.items.count
            let name = drop.destinationFolder.lastPathComponent
            Text(count == 1
                 ? "What would you like to do with \"\(drop.items[0].name)\" in \"\(name)\"?"
                 : "What would you like to do with \(count) items in \"\(name)\"?")
        }
        .confirmationDialog(
            "Move to Trash",
            isPresented: $showDeleteConfirmation
        ) {
            Button("Move to Trash", role: .destructive) {
                Task {
                    await fm.trashSelectedItems()
                    await appState.refreshAllPanels()
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            let items = fm.selectedItems
            if items.count == 1 {
                Text("Are you sure you want to move \"\(items[0].name)\" to the Trash?")
            } else {
                Text("Are you sure you want to move \(items.count) items to the Trash?")
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
            "Move or Copy?",
            isPresented: $showCloudDropConfirmation,
            presenting: pendingCloudDrop
        ) { drop in
            Button("Copy Here") {
                let sourceItems = drop.sourceItems
                let targetDir = drop.targetDirectory
                let sourceAccountId = drop.sourceAccountId
                pendingCloudDrop = nil
                let transfer = TransferProgress(operation: "Copying", totalItems: sourceItems.count)
                appState.addTransfer(transfer, panel: panelSide)
                transfer.task = Task {
                    let cloudVM = appState.cloudFileManager(for: sourceAccountId)
                    cloudVM.conflictDirection = incomingDirection
                    await cloudVM.downloadItems(sourceItems, to: targetDir, progress: transfer)
                    await fm.refresh()
                }
            }
            Button("Move Here") {
                let sourceItems = drop.sourceItems
                let targetDir = drop.targetDirectory
                let sourceAccountId = drop.sourceAccountId
                pendingCloudDrop = nil
                let transfer = TransferProgress(operation: "Moving", totalItems: sourceItems.count)
                appState.addTransfer(transfer, panel: panelSide)
                transfer.task = Task {
                    let cloudVM = appState.cloudFileManager(for: sourceAccountId)
                    cloudVM.conflictDirection = incomingDirection
                    await cloudVM.downloadItems(sourceItems, to: targetDir, progress: transfer)
                    await cloudVM.deleteItems(sourceItems)
                    await fm.refresh()
                }
            }
            Button("Cancel", role: .cancel) {
                pendingCloudDrop = nil
            }
        } message: { drop in
            let count = drop.sourceItems.count
            let name = drop.targetDirectory.lastPathComponent
            Text(count == 1
                 ? "What would you like to do with \"\(drop.sourceItems[0].name)\" in \"\(name)\"?"
                 : "What would you like to do with \(count) items in \"\(name)\"?")
        }
        .alert("New Folder", isPresented: $showNewFolderDialog) {
            TextField("Folder name", text: $newFolderName)
            Button("Create") {
                let name = newFolderName
                Task { await fm.createNewFolder(named: name) }
            }
            Button("Cancel", role: .cancel) {}
        }
        .alert("Rename", isPresented: $showRenameDialog) {
            TextField("Name", text: $renameText)
            Button("Rename") {
                if let item = renameItem {
                    let newName = renameText
                    Task { await fm.renameItem(item, to: newName) }
                }
            }
            Button("Cancel", role: .cancel) {}
        }
        .onReceive(NotificationCenter.default.publisher(for: .menuNewFolder)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            newFolderName = "New Folder"
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
            guard !fm.selectedItems.isEmpty else { return }
            showDeleteConfirmation = true
        }
        .onReceive(NotificationCenter.default.publisher(for: .menuCopyToOtherPanel)) { _ in
            guard appState.activePanel == panelSide, !appState.isActivePanelCloud else { return }
            let items = fm.selectedItems
            guard !items.isEmpty else { return }
            let otherSide: PanelSide = panelSide == .left ? .right : .left
            let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
            appState.addTransfer(transfer, panel: otherSide)
            if let cloudId = appState.cloudAccountId(for: otherSide) {
                let cloudVM = appState.cloudFileManager(for: cloudId)
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
                let cloudVM = appState.cloudFileManager(for: cloudId)
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

        fm.pendingDrop = FileManagerViewModel.PendingDrop(items: droppedItems, destinationFolder: target)
        showDropConfirmation = true
        return true
    }

    /// Drop a cloud-panel drag onto a breadcrumb component. Reuses
    /// `cloudDragSource*` state set by the cloud panel's drag-start hook.
    private func handlePathDropCloudPromise(target: URL) -> Bool {
        guard !appState.cloudDragSourceItems.isEmpty,
              let sourceAccountId = appState.cloudDragSourceAccountId else { return false }
        pendingCloudDrop = PendingCloudDrop(
            sourceItems: appState.cloudDragSourceItems,
            sourceAccountId: sourceAccountId,
            targetDirectory: target
        )
        showCloudDropConfirmation = true
        return true
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
                    selectedIDs: Bindable(fm).selectedItemIDs,
                    quickLookController: fm.quickLookController,
                    onDoubleClick: { item in
                        Task { await fm.openItem(item) }
                    },
                    onDrop: { droppedItems, targetURL in
                        fm.pendingDrop = FileManagerViewModel.PendingDrop(
                            items: droppedItems,
                            destinationFolder: targetURL
                        )
                        showDropConfirmation = true
                    },
                    onKeySpace: {
                        fm.toggleQuickLook()
                    },
                    onDelete: {
                        guard !fm.selectedItems.isEmpty else { return }
                        showDeleteConfirmation = true
                    },
                    onCopyToOtherPanel: { items in
                        let otherSide: PanelSide = panelSide == .left ? .right : .left
                        let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
                        appState.addTransfer(transfer, panel: otherSide)
                        fm.conflictDirection = outgoingDirection
                        if let cloudId = appState.cloudAccountId(for: otherSide) {
                            let cloudVM = appState.cloudFileManager(for: cloudId)
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
                    },
                    onMoveToOtherPanel: { items in
                        let otherSide: PanelSide = panelSide == .left ? .right : .left
                        let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                        appState.addTransfer(transfer, panel: otherSide)
                        fm.conflictDirection = outgoingDirection
                        if let cloudId = appState.cloudAccountId(for: otherSide) {
                            let cloudVM = appState.cloudFileManager(for: cloudId)
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
                    },
                    onCalculateFolderSize: { folder in
                        appState.calculateFolderSize(for: folder.url, panel: panelSide)
                    },
                    onAddToFavorites: { folder in
                        appState.addFavorite(url: folder.url)
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
                            pendingCloudDrop = PendingCloudDrop(
                                sourceItems: appState.cloudDragSourceItems,
                                sourceAccountId: sourceAccountId,
                                targetDirectory: targetDir
                            )
                            showCloudDropConfirmation = true
                        }
                        Task { await fm.refresh() }
                    },
                    onCreateFolder: {
                        newFolderName = "New Folder"
                        showNewFolderDialog = true
                    },
                    onRename: { item in
                        renameItem = item
                        renameText = item.name
                        showRenameDialog = true
                    }
                )
                .onChange(of: fm.selectedItemIDs) {
                    fm.updateQuickLookSelection()
                    fm.recalculateSelectionSize()
                }
                .overlay {
                    if fm.filteredItems.isEmpty {
                        ContentUnavailableView("No Files", systemImage: "folder", description: Text("This folder is empty"))
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
            Text("\(fileCount) files, \(folderCount) folders — \(ByteCountFormatter.string(fromByteCount: totalSize, countStyle: .file))")
            if !selected.isEmpty {
                Text("·")
                if fm.isCalculatingSelectionSize {
                    Text("Selected: \(selected.count) item\(selected.count == 1 ? "" : "s"), Calculating…")
                } else if let size = fm.selectionSize {
                    Text("Selected: \(selected.count) item\(selected.count == 1 ? "" : "s"), \(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))")
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
        var url = fm.currentDirectory
        while url.path() != "/" {
            components.insert((url.lastPathComponent, url), at: 0)
            url = url.deletingLastPathComponent()
        }
        components.insert(("/", URL(filePath: "/")), at: 0)
        return components
    }
}

