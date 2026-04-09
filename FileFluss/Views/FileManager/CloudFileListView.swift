import SwiftUI

struct CloudFileListView: View {
    let panelSide: PanelSide
    let accountId: UUID
    @Environment(AppState.self) private var appState
    @State private var showDeleteConfirmation = false
    @State private var showConflict = false
    @State private var showDropConfirmation = false
    @State private var pendingUploadURLs: [URL]?
    @State private var showCloudToCloudDropConfirmation = false
    @State private var pendingCloudToCloudDrop: PendingCloudToCloudDrop?
    @State private var showNewFolderDialog = false
    @State private var newFolderName = ""
    @State private var showRenameDialog = false
    @State private var renameText = ""
    @State private var renameCloudItem: CloudFileItem?

    struct PendingCloudToCloudDrop {
        let sourceItems: [CloudFileItem]
        let sourceAccountId: UUID
    }

    private var vm: CloudFileManagerViewModel {
        appState.cloudFileManager(for: accountId)
    }

    private var incomingDirection: ConflictDirection {
        panelSide == .right ? .leftToRight : .rightToLeft
    }

    private var outgoingDirection: ConflictDirection {
        panelSide == .left ? .leftToRight : .rightToLeft
    }

    var body: some View {
        bodyWithDialogs
            .onReceive(NotificationCenter.default.publisher(for: .menuNewFolder)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                guard appState.canCreateFolderInActivePanel else { return }
                newFolderName = "New Folder"
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
                guard !vm.selectedItems.isEmpty else { return }
                showDeleteConfirmation = true
            }
            .onReceive(NotificationCenter.default.publisher(for: .menuCopyToOtherPanel)) { _ in
                guard appState.activePanel == panelSide, appState.cloudAccountId(for: panelSide) == accountId else { return }
                let items = vm.selectedItems
                guard !items.isEmpty else { return }
                let otherSide: PanelSide = panelSide == .left ? .right : .left
                vm.conflictDirection = outgoingDirection
                if let otherCloudId = appState.cloudAccountId(for: otherSide) {
                    let otherCloudVM = appState.cloudFileManager(for: otherCloudId)
                    otherCloudVM.conflictDirection = outgoingDirection
                    let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    Task { await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: false) }
                } else {
                    let otherFM = appState.fileManager(for: otherSide)
                    let transfer = TransferProgress(operation: "Downloading", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    Task {
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
                    let otherCloudVM = appState.cloudFileManager(for: otherCloudId)
                    otherCloudVM.conflictDirection = outgoingDirection
                    let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    Task { await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: true) }
                } else {
                    let otherFM = appState.fileManager(for: otherSide)
                    let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    Task {
                        await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                        await vm.deleteItems(items)
                        await otherFM.refresh()
                    }
                }
            }
    }

    private var bodyWithDialogs: some View {
        bodyWithDropDialogs
        .confirmationDialog("Delete from Cloud", isPresented: $showDeleteConfirmation) {
            Button("Delete", role: .destructive) {
                Task { await vm.deleteSelectedItems() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            let items = vm.selectedItems
            if items.count == 1 {
                Text("Are you sure you want to delete \"\(items[0].name)\" from the cloud?")
            } else {
                Text("Are you sure you want to delete \(items.count) items from pCloud?")
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
        .alert("New Folder", isPresented: $showNewFolderDialog) {
            TextField("Folder name", text: $newFolderName)
            Button("Create") {
                let name = newFolderName
                Task { await vm.createNewFolder(named: name) }
            }
            Button("Cancel", role: .cancel) {}
        }
        .alert("Rename", isPresented: $showRenameDialog) {
            TextField("Name", text: $renameText)
            Button("Rename") {
                if let item = renameCloudItem {
                    let newName = renameText
                    Task { await vm.renameItem(item, to: newName) }
                }
            }
            Button("Cancel", role: .cancel) {}
        }
    }

    private var bodyWithDropDialogs: some View {
        mainContent
        .task(id: accountId) {
            await vm.loadDirectory()
        }
        .confirmationDialog("Move or Copy?", isPresented: $showDropConfirmation, presenting: pendingUploadURLs) { urls in
            Button("Copy Here") {
                let transfer = TransferProgress(operation: "Copying", totalItems: urls.count)
                appState.addTransfer(transfer, panel: panelSide)
                pendingUploadURLs = nil
                vm.conflictDirection = incomingDirection
                Task { await vm.uploadFiles(from: urls, progress: transfer) }
            }
            Button("Move Here") {
                let transfer = TransferProgress(operation: "Moving", totalItems: urls.count)
                appState.addTransfer(transfer, panel: panelSide)
                pendingUploadURLs = nil
                vm.conflictDirection = incomingDirection
                Task {
                    await vm.uploadFiles(from: urls, progress: transfer)
                    for url in urls { try? FileManager.default.removeItem(at: url) }
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    await appState.fileManager(for: otherSide).refresh()
                }
            }
            Button("Cancel", role: .cancel) { pendingUploadURLs = nil }
        } message: { urls in
            let providerName = appState.syncManager.accountFor(id: accountId)?.providerType.displayName ?? "Cloud"
            let name = vm.currentPath == "/" ? providerName : (vm.currentPath as NSString).lastPathComponent
            if urls.count == 1 {
                Text("What would you like to do with \"\(urls[0].lastPathComponent)\" in \"\(name)\"?")
            } else {
                Text("What would you like to do with \(urls.count) items in \"\(name)\"?")
            }
        }
        .confirmationDialog("Move or Copy?", isPresented: $showCloudToCloudDropConfirmation, presenting: pendingCloudToCloudDrop) { drop in
            Button("Copy Here") {
                let sourceItems = drop.sourceItems
                let sourceAccountId = drop.sourceAccountId
                pendingCloudToCloudDrop = nil
                let sourceVM = appState.cloudFileManager(for: sourceAccountId)
                sourceVM.conflictDirection = incomingDirection
                vm.conflictDirection = incomingDirection
                let transfer = TransferProgress(operation: "Copying", totalItems: sourceItems.count)
                appState.addTransfer(transfer, panel: panelSide)
                Task { await Self.cloudToCloudTransfer(items: sourceItems, from: sourceVM, to: vm, progress: transfer, deleteFromSource: false) }
            }
            Button("Move Here") {
                let sourceItems = drop.sourceItems
                let sourceAccountId = drop.sourceAccountId
                pendingCloudToCloudDrop = nil
                let sourceVM = appState.cloudFileManager(for: sourceAccountId)
                sourceVM.conflictDirection = incomingDirection
                vm.conflictDirection = incomingDirection
                let transfer = TransferProgress(operation: "Moving", totalItems: sourceItems.count)
                appState.addTransfer(transfer, panel: panelSide)
                Task { await Self.cloudToCloudTransfer(items: sourceItems, from: sourceVM, to: vm, progress: transfer, deleteFromSource: true) }
            }
            Button("Cancel", role: .cancel) { pendingCloudToCloudDrop = nil }
        } message: { drop in
            let count = drop.sourceItems.count
            let providerName = appState.syncManager.accountFor(id: accountId)?.providerType.displayName ?? "Cloud"
            let name = vm.currentPath == "/" ? providerName : (vm.currentPath as NSString).lastPathComponent
            Text(count == 1
                 ? "What would you like to do with \"\(drop.sourceItems[0].name)\" in \"\(name)\"?"
                 : "What would you like to do with \(count) items in \"\(name)\"?")
        }
    }

    private var mainContent: some View {
        VStack(spacing: 0) {
            cloudPathBar
            Divider()
            cloudFileArea
            Divider()
            cloudStatusFooter
        }
    }

    private var cloudStatusFooter: some View {
        let items = vm.filteredItems
        let fileCount = items.filter { !$0.isDirectory }.count
        let folderCount = items.filter { $0.isDirectory }.count
        let totalSize = items.filter { !$0.isDirectory }.reduce(Int64(0)) { $0 + $1.size }
        let selected = vm.selectedItems

        return HStack(spacing: 4) {
            Text("\(fileCount) files, \(folderCount) folders — \(ByteCountFormatter.string(fromByteCount: totalSize, countStyle: .file))")
            if !selected.isEmpty {
                Text("·")
                if vm.isCalculatingSelectionSize {
                    Text("Selected: \(selected.count) item\(selected.count == 1 ? "" : "s"), Calculating…")
                } else if let size = vm.selectionSize {
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
                    Button(component.name) {
                        Task { await vm.navigateTo(component.path) }
                    }
                    .buttonStyle(.plain)
                    .font(.system(.body, design: .default, weight: .medium))

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

    @ViewBuilder
    private var cloudFileArea: some View {
        if vm.isLoading && vm.items.isEmpty {
            ProgressView("Loading...")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if let error = vm.error {
            ContentUnavailableView("Error", systemImage: "exclamationmark.triangle", description: Text(error))
        } else {
            NativeCloudFileList(
                items: vm.filteredItems,
                panelSide: panelSide,
                selectedIDs: Bindable(vm).selectedItemIDs,
                quickLookController: vm.quickLookController,
                onDoubleClick: { item in
                    Task { await vm.openItem(item) }
                },
                onDrop: { urls in
                    pendingUploadURLs = urls
                    showDropConfirmation = true
                },
                onKeySpace: {
                    vm.toggleQuickLook()
                },
                onDelete: {
                    guard !vm.selectedItems.isEmpty else { return }
                    showDeleteConfirmation = true
                },
                onCopyToOtherPanel: { items in
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    vm.conflictDirection = outgoingDirection
                    if let otherCloudId = appState.cloudAccountId(for: otherSide) {
                        let otherCloudVM = appState.cloudFileManager(for: otherCloudId)
                        otherCloudVM.conflictDirection = outgoingDirection
                        let transfer = TransferProgress(operation: "Copying", totalItems: items.count)
                        appState.addTransfer(transfer, panel: otherSide)
                        Task {
                            await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: false)
                        }
                    } else {
                        let otherFM = appState.fileManager(for: otherSide)
                        let transfer = TransferProgress(operation: "Downloading", totalItems: items.count)
                        appState.addTransfer(transfer, panel: otherSide)
                        Task {
                            await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                            await otherFM.refresh()
                        }
                    }
                },
                onMoveToOtherPanel: { items in
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    vm.conflictDirection = outgoingDirection
                    if let otherCloudId = appState.cloudAccountId(for: otherSide) {
                        let otherCloudVM = appState.cloudFileManager(for: otherCloudId)
                        otherCloudVM.conflictDirection = outgoingDirection
                        let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                        appState.addTransfer(transfer, panel: otherSide)
                        Task {
                            await Self.cloudToCloudTransfer(items: items, from: vm, to: otherCloudVM, progress: transfer, deleteFromSource: true)
                        }
                    } else {
                        let otherFM = appState.fileManager(for: otherSide)
                        let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                        appState.addTransfer(transfer, panel: otherSide)
                        Task {
                            await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                            await vm.deleteItems(items)
                            await otherFM.refresh()
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
                        name: folder.name
                    )
                },
                onSortChanged: { key, ascending in
                    switch key {
                    case "name": vm.sortOrder = .name
                    case "date": vm.sortOrder = .date
                    case "size": vm.sortOrder = .size
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
                },
                onDragSessionEnded: {
                    appState.cloudDragSourceItems = []
                    appState.cloudDragSourceAccountId = nil
                },
                onReceiveCloudDrop: {
                    if !appState.cloudDragSourceItems.isEmpty,
                       let sourceAccountId = appState.cloudDragSourceAccountId,
                       sourceAccountId != accountId {
                        pendingCloudToCloudDrop = PendingCloudToCloudDrop(
                            sourceItems: appState.cloudDragSourceItems,
                            sourceAccountId: sourceAccountId
                        )
                        showCloudToCloudDropConfirmation = true
                    }
                },
                onCreateFolder: {
                    newFolderName = "New Folder"
                    showNewFolderDialog = true
                },
                onRename: { item in
                    renameCloudItem = item
                    renameText = item.name
                    showRenameDialog = true
                },
                canCreateFolder: appState.syncManager.accountFor(id: accountId)?.providerType != .wordpress
            )
            .onChange(of: vm.selectedItemIDs) {
                vm.updateQuickLookSelection()
                vm.recalculateSelectionSize()
            }
            .overlay {
                if vm.filteredItems.isEmpty && !vm.isLoading {
                    ContentUnavailableView("Empty Folder", systemImage: "folder", description: Text("This cloud folder is empty"))
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
        progress: TransferProgress?,
        deleteFromSource: Bool
    ) async {
        // Phase 0: Pre-flight conflict resolution (no downloads yet)
        let resolutions = await targetVM.preFlightConflictCheck(sourceItems: items)

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

        // Phase 1: Download only non-skipped items
        if let progress {
            progress.isCloudToCloud = true
            progress.currentPhase = .downloading
            progress.downloadStartTime = Date()
        }

        await sourceVM.downloadItems(itemsToTransfer, to: tempDir, progress: progress, skipConflictCheck: true)

        if let progress {
            progress.downloadEndTime = Date()
            progress.downloadBytes = progress.totalBytes
        }

        // Phase 2: Delete items that need replacing, then upload
        let provider = await SyncEngine.shared.provider(for: targetVM.accountId)
        for item in itemsToTransfer {
            if resolutionByName[item.name] == .replace {
                // Find and delete existing item on target
                if let existing = targetVM.items.first(where: { $0.name == item.name }) {
                    try? await provider?.deleteItem(at: existing.path)
                }
            }
        }

        // Build local URLs for upload, renaming "keepBoth" items
        let existingNames = Set(targetVM.items.map(\.name))
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
            progress.totalBytes = 0
            progress.uploadStartTime = Date()
        }

        // Upload with conflict check skipped — we already handled conflicts
        await targetVM.uploadFiles(from: localURLs, progress: progress, skipConflictCheck: true)

        if let progress {
            progress.uploadEndTime = Date()
            progress.uploadBytes = progress.totalBytes
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
