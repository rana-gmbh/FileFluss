import SwiftUI

struct FileListView: View {
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState
    @State private var showDropConfirmation: Bool = false
    @State private var showDeleteConfirmation: Bool = false

    private var fm: FileManagerViewModel {
        appState.fileManager(for: panelSide)
    }

    var body: some View {
        VStack(spacing: 0) {
            pathBar
            Divider()
            fileArea
        }
        .confirmationDialog(
            "Move or Copy?",
            isPresented: $showDropConfirmation,
            presenting: fm.pendingDrop
        ) { drop in
            Button("Move Here") {
                let items = drop.items
                let dest = drop.destinationFolder
                Task {
                    await fm.performMove(items: items, to: dest)
                    await appState.refreshAllPanels()
                }
            }
            Button("Copy Here") {
                let items = drop.items
                let dest = drop.destinationFolder
                Task {
                    await fm.performCopy(items: items, to: dest)
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
    }

    private var pathBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                ForEach(pathComponents, id: \.url) { component in
                    Button(component.name) {
                        Task { await fm.navigateTo(component.url) }
                    }
                    .buttonStyle(.plain)
                    .font(.system(.body, design: .default, weight: .medium))

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

    private var fileArea: some View {
        Group {
            if fm.isLoading {
                ProgressView()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if fm.filteredItems.isEmpty {
                ContentUnavailableView("No Files", systemImage: "folder", description: Text("This folder is empty"))
            } else {
                NativeFileList(
                    items: fm.filteredItems,
                    currentDirectory: fm.currentDirectory,
                    panelSide: panelSide,
                    selectedIDs: Bindable(fm).selectedItemIDs,
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
                        let otherFM = appState.fileManager(for: panelSide == .left ? .right : .left)
                        Task {
                            await fm.performCopy(items: items, to: otherFM.currentDirectory)
                            await appState.refreshAllPanels()
                        }
                    },
                    onMoveToOtherPanel: { items in
                        let otherFM = appState.fileManager(for: panelSide == .left ? .right : .left)
                        Task {
                            await fm.performMove(items: items, to: otherFM.currentDirectory)
                            await appState.refreshAllPanels()
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
                        case "size": fm.sortOrder = .size
                        default: break
                        }
                        fm.sortAscending = ascending
                    },
                    onBecameActive: {
                        appState.activePanel = panelSide
                    }
                )
                .onChange(of: fm.selectedItemIDs) {
                    fm.updateQuickLookSelection()
                }
                .background {
                    QuickLookBridge(controller: fm.quickLookController)
                        .frame(width: 0, height: 0)
                }
            }
        }
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
