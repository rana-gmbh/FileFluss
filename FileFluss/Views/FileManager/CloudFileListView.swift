import SwiftUI

struct CloudFileListView: View {
    let panelSide: PanelSide
    let accountId: UUID
    @Environment(AppState.self) private var appState
    @State private var showDeleteConfirmation = false
    @State private var showOverwriteConfirmation = false
    @State private var showDropConfirmation = false
    @State private var pendingUploadURLs: [URL]?

    private var vm: CloudFileManagerViewModel {
        appState.cloudFileManager(for: accountId)
    }

    var body: some View {
        VStack(spacing: 0) {
            cloudPathBar
            Divider()
            cloudFileArea
        }
        .task {
            if vm.items.isEmpty {
                await vm.loadDirectory()
            }
        }
        .confirmationDialog(
            "Delete from Cloud",
            isPresented: $showDeleteConfirmation
        ) {
            Button("Delete", role: .destructive) {
                Task { await vm.deleteSelectedItems() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            let items = vm.selectedItems
            if items.count == 1 {
                Text("Are you sure you want to delete \"\(items[0].name)\" from pCloud?")
            } else {
                Text("Are you sure you want to delete \(items.count) items from pCloud?")
            }
        }
        .confirmationDialog(
            "File Already Exists",
            isPresented: $showOverwriteConfirmation,
            presenting: vm.pendingOverwrite
        ) { overwrite in
            Button("Overwrite", role: .destructive) {
                let items = overwrite.items
                let dest = overwrite.localDirectory
                let progress = overwrite.progress
                let deleteAfter = overwrite.deleteAfter
                vm.pendingOverwrite = nil
                Task {
                    await vm.downloadItems(items, to: dest, progress: progress, overwrite: true)
                    if deleteAfter {
                        await vm.deleteItems(items)
                    }
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    await appState.fileManager(for: otherSide).refresh()
                }
            }
            Button("Cancel", role: .cancel) {
                vm.pendingOverwrite?.progress?.isComplete = true
                vm.pendingOverwrite = nil
            }
        } message: { overwrite in
            let names = overwrite.conflicting
            if names.count == 1 {
                Text("\"\(names[0])\" already exists locally. Do you want to overwrite it?")
            } else {
                Text("\(names.count) files already exist locally. Do you want to overwrite them?")
            }
        }
        .onChange(of: vm.pendingOverwrite != nil) { _, hasOverwrite in
            if hasOverwrite { showOverwriteConfirmation = true }
        }
        .confirmationDialog(
            "Move or Copy?",
            isPresented: $showDropConfirmation,
            presenting: pendingUploadURLs
        ) { urls in
            Button("Move Here") {
                let transfer = TransferProgress(operation: "Moving", totalItems: urls.count)
                appState.addTransfer(transfer, panel: panelSide)
                pendingUploadURLs = nil
                Task {
                    await vm.uploadFiles(from: urls, progress: transfer)
                    for url in urls {
                        try? FileManager.default.removeItem(at: url)
                    }
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    await appState.fileManager(for: otherSide).refresh()
                }
            }
            Button("Copy Here") {
                let transfer = TransferProgress(operation: "Copying", totalItems: urls.count)
                appState.addTransfer(transfer, panel: panelSide)
                pendingUploadURLs = nil
                Task {
                    await vm.uploadFiles(from: urls, progress: transfer)
                }
            }
            Button("Cancel", role: .cancel) {
                pendingUploadURLs = nil
            }
        } message: { urls in
            let name = vm.currentPath == "/" ? "pCloud" : (vm.currentPath as NSString).lastPathComponent
            if urls.count == 1 {
                Text("What would you like to do with \"\(urls[0].lastPathComponent)\" in \"\(name)\"?")
            } else {
                Text("What would you like to do with \(urls.count) items in \"\(name)\"?")
            }
        }
    }

    private var cloudPathBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                Image(systemName: "cloud.fill")
                    .foregroundStyle(.secondary)
                    .font(.caption)

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
        } else if vm.filteredItems.isEmpty {
            ContentUnavailableView("Empty Folder", systemImage: "folder", description: Text("This cloud folder is empty"))
        } else {
            NativeCloudFileList(
                items: vm.filteredItems,
                panelSide: panelSide,
                selectedIDs: Bindable(vm).selectedItemIDs,
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
                    let otherFM = appState.fileManager(for: otherSide)
                    let transfer = TransferProgress(operation: "Downloading", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    Task {
                        await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                        await otherFM.refresh()
                    }
                },
                onMoveToOtherPanel: { items in
                    let otherSide: PanelSide = panelSide == .left ? .right : .left
                    let otherFM = appState.fileManager(for: otherSide)
                    let transfer = TransferProgress(operation: "Moving", totalItems: items.count)
                    appState.addTransfer(transfer, panel: otherSide)
                    Task {
                        await vm.downloadItems(items, to: otherFM.currentDirectory, progress: transfer)
                        await vm.deleteItems(items)
                        await otherFM.refresh()
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
                }
            )
            .onChange(of: vm.selectedItemIDs) {
                vm.updateQuickLookSelection()
            }
            .background {
                QuickLookBridge(controller: vm.quickLookController)
                    .frame(width: 0, height: 0)
            }
            .overlay(alignment: .top) {
                if vm.isLoading {
                    ProgressView()
                        .scaleEffect(0.5)
                        .padding(4)
                }
            }
        }
    }

    private var pathComponents: [(name: String, path: String)] {
        var components: [(String, String)] = [("pCloud", "/")]
        let parts = vm.currentPath.split(separator: "/", omittingEmptySubsequences: true)
        var accumulated = ""
        for part in parts {
            accumulated += "/\(part)"
            components.append((String(part), accumulated))
        }
        return components
    }
}
