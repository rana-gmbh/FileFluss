import SwiftUI

struct SearchPopupView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            searchHeader
            Divider()
            offlineToggleBar
            Divider()
            filterBar
            Divider()
            resultsList
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onChange(of: appState.searchVM.searchText) {
            triggerSearch()
        }
    }

    private var searchHeader: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(.secondary)
                .font(.system(size: 14))

            TextField("Search files across all sources...", text: Bindable(appState.searchVM).searchText)
                .textFieldStyle(.plain)
                .font(.system(size: 15))
                .onSubmit { triggerSearch() }

            searchStatusView

            if !appState.searchVM.searchText.isEmpty {
                Button {
                    appState.searchVM.clear()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Clear search")
            }

            Button {
                dismiss()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(.secondary)
                    .frame(width: 22, height: 22)
                    .background(.quaternary, in: Circle())
            }
            .buttonStyle(.plain)
            .help("Close (Esc)")
            .keyboardShortcut(.cancelAction)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private var searchStatusView: some View {
        switch appState.searchVM.status {
        case .idle:
            EmptyView()
        case .searching(let completed, let total):
            HStack(spacing: 4) {
                ProgressView()
                    .scaleEffect(0.5)
                    .frame(width: 14, height: 14)
                if total > 0 {
                    Text("\(completed)/\(total)")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }
        case .complete(let count):
            Text("\(count) results")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
        }
    }

    private var offlineToggleBar: some View {
        HStack(spacing: 8) {
            Toggle(isOn: Bindable(appState.searchVM).includeOffline) {
                Label("Show offline results", systemImage: "wifi.slash")
                    .font(.system(size: 11))
            }
            .toggleStyle(.switch)
            .controlSize(.mini)
            .onChange(of: appState.searchVM.includeOffline) {
                triggerSearch()
            }
            Spacer()
            Text("Includes indexed drives & disconnected accounts")
                .font(.system(size: 10))
                .foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 6)
    }

    private var filterBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                let vm = appState.searchVM
                let sources = vm.availableSources

                if sources.count > 1 {
                    ForEach(sources, id: \.filter) { source in
                        FilterChip(
                            label: source.label,
                            providerType: source.providerType,
                            isSelected: vm.sourceFilter == source.filter
                        ) {
                            vm.sourceFilter = source.filter
                        }
                    }
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
        }
        .frame(height: appState.searchVM.availableSources.count > 1 ? nil : 0)
        .clipped()
    }

    private var resultsList: some View {
        // Force this region to claim all remaining vertical space so the
        // header stays pinned to the top regardless of whether we're
        // showing the empty-state view or a populated results list.
        // Without this, the empty state would shrink-wrap and the parent
        // VStack would vertically center, making the header appear to
        // jump up the moment results arrive.
        Group {
            let vm = appState.searchVM
            if vm.searchText.isEmpty {
                ContentUnavailableView(
                    "Search All Sources",
                    systemImage: "magnifyingglass",
                    description: Text("Search across local files and all connected cloud accounts")
                )
            } else if vm.filteredResults.isEmpty && vm.status != .idle {
                if case .searching = vm.status {
                    ProgressView("Searching...")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    ContentUnavailableView.search(text: vm.searchText)
                }
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(vm.groupedResults, id: \.source) { group in
                            SearchGroupHeader(
                                source: group.source,
                                icon: group.icon,
                                providerType: group.providerType,
                                count: group.items.count
                            )
                            ForEach(group.items) { item in
                                SearchResultRow(item: item)
                                    .contextMenu {
                                        resultContextMenu(for: item)
                                    }
                                    .onTapGesture(count: 2) {
                                        openInPanel(item, side: appState.activePanel)
                                    }
                                if item.id != group.items.last?.id {
                                    Divider()
                                        .padding(.leading, 40)
                                }
                            }
                            Divider()
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder
    private func resultContextMenu(for item: SearchResultItem) -> some View {
        Button {
            openInPanel(item, side: .left)
        } label: {
            Label("Open in Left Panel", systemImage: "sidebar.left")
        }

        Button {
            openInPanel(item, side: .right)
        } label: {
            Label("Open in Right Panel", systemImage: "sidebar.right")
        }
    }

    private func openInPanel(_ item: SearchResultItem, side: PanelSide) {
        switch item {
        case .offline:
            if let nav = item.offlineNavigation {
                appState.setSidebarSelection(
                    .offlineFolder(sourceId: nav.sourceId, path: nav.parentPath),
                    for: side
                )
                appState.activePanel = side
            }
            return
        case .local(let fileItem):
            let parentURL: URL
            if fileItem.isDirectory {
                parentURL = fileItem.url
            } else {
                parentURL = fileItem.url.deletingLastPathComponent()
            }
            appState.setSidebarSelection(.location(parentURL), for: side)
            let fm = appState.fileManager(for: side)
            Task {
                await fm.navigateTo(parentURL)
                if !fileItem.isDirectory {
                    fm.selectedItemIDs = [fileItem.id]
                }
            }

        case .cloud(let cloudItem, let accountId, _):
            let parentPath: String
            if cloudItem.isDirectory {
                parentPath = cloudItem.path
            } else {
                parentPath = (cloudItem.path as NSString).deletingLastPathComponent
            }
            appState.setSidebarSelection(.cloudAccount(
                CloudAccount(id: accountId, providerType: .pCloud, displayName: "", isConnected: true, rootPath: "/")
            ), for: side)
            if let account = appState.syncManager.accountFor(id: accountId) {
                appState.setSidebarSelection(.cloudAccount(account), for: side)
            }
            let cloudVM = appState.cloudFileManager(for: accountId, side: side)
            Task {
                await cloudVM.navigateTo(parentPath)
                if !cloudItem.isDirectory {
                    cloudVM.selectedItemIDs = [cloudItem.id]
                }
            }
        }

        appState.activePanel = side
        // Leave the search window open so the user can pick more results.
        // Only the close button / Esc / red traffic light should dismiss.
    }

    private func triggerSearch() {
        let localDir = appState.activeFileManager.currentDirectory
        // Online-only accounts feed the live search; anything the user has
        // forced into offline mode (or that's simply disconnected) goes
        // through the offline-search path against `cloud_files`.
        let connectedAccounts = appState.syncManager.accounts
            .filter { $0.isConnected && !$0.isOfflineMode }
            .map { (id: $0.id, name: $0.providerType.displayName) }
        let offlineAccounts = appState.syncManager.accounts
            .filter { !$0.isConnected || $0.isOfflineMode }
            .map { (id: $0.id, name: $0.displayName) }
        let offlineDrives = appState.driveMonitor.drives
            .filter { !appState.driveMonitor.isOnline($0) && $0.lastIndexed != nil }
            .map { (id: $0.id, name: $0.displayName) }
        // Fan out the search to every currently-mounted external/network
        // drive so users find files on them even when neither panel is
        // sitting on the drive. Skip any drive whose mount path is already
        // a prefix of the active local directory to avoid duplicate hits.
        let activePath = localDir.path
        var extraRoots: [(url: URL, name: String)] = []
        for drive in appState.driveMonitor.drives {
            guard let url = appState.driveMonitor.mountURL(for: drive.id) else { continue }
            if activePath.hasPrefix(url.path) { continue }
            extraRoots.append((url: url, name: drive.displayName))
        }
        appState.searchVM.performSearch(
            localDirectory: localDir,
            allAccounts: connectedAccounts,
            extraLocalRoots: extraRoots,
            offlineCloudAccounts: offlineAccounts,
            offlineDrives: offlineDrives
        )
    }
}

// MARK: - Subviews

private struct FilterChip: View {
    let label: String
    let providerType: CloudProviderType?
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4) {
                if let providerType {
                    CloudProviderIcon(providerType: providerType, size: 12)
                }
                Text(label)
                    .font(.system(size: 11, weight: isSelected ? .semibold : .regular))
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .background(isSelected ? Color.accentColor.opacity(0.15) : Color.clear)
            .foregroundStyle(isSelected ? Color.accentColor : .secondary)
            .clipShape(Capsule())
            .overlay(Capsule().stroke(isSelected ? Color.accentColor.opacity(0.3) : Color.secondary.opacity(0.2), lineWidth: 1))
        }
        .buttonStyle(.plain)
    }
}

private struct SearchGroupHeader: View {
    let source: String
    let icon: String?
    let providerType: CloudProviderType?
    let count: Int

    var body: some View {
        HStack(spacing: 6) {
            if let providerType {
                CloudProviderIcon(providerType: providerType, size: 14)
            } else if let icon {
                Image(systemName: icon)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            }

            Text(source)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.secondary)

            Text("(\(count))")
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)

            Spacer()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 6)
        .background(.quaternary.opacity(0.5))
    }
}

private struct SearchResultRow: View {
    let item: SearchResultItem

    private var iconImage: NSImage {
        if item.isDirectory { return FileTypeIcon.folderIcon() }
        if let colorful = FreeFileIcon.icon(forFilename: item.name) { return colorful }
        switch item {
        case .local(let f): return FileTypeIcon.icon(for: f.contentType)
        case .cloud(let f, _, _): return FileTypeIcon.icon(forFilename: f.name)
        case .offline(let n, _, _, _, _, _, _, _): return FileTypeIcon.icon(forFilename: n)
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            Image(nsImage: iconImage)
                .resizable()
                .interpolation(.high)
                .frame(width: 20, height: 20)
                .frame(width: 24, alignment: .center)
                .opacity(item.isOffline ? 0.55 : 1.0)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(item.name)
                        .font(.system(size: 13))
                        .italic(item.isOffline)
                        .foregroundStyle(item.isOffline ? .secondary : .primary)
                        .lineLimit(1)
                    if item.isOffline {
                        Text("Offline")
                            .font(.system(size: 9, weight: .medium))
                            .padding(.horizontal, 5)
                            .padding(.vertical, 1)
                            .background(Color.secondary.opacity(0.18), in: Capsule())
                            .foregroundStyle(.secondary)
                    }
                }

                Text(item.locationDescription)
                    .font(.system(size: 10))
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }

            Spacer()

            if !item.isDirectory {
                Text(item.formattedSize)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .frame(width: 70, alignment: .trailing)
            }

            Text(item.formattedDate)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .frame(width: 120, alignment: .trailing)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 6)
        .contentShape(Rectangle())
    }
}

extension SearchViewModel.SourceFilter: Equatable {
    static func == (lhs: Self, rhs: Self) -> Bool {
        switch (lhs, rhs) {
        case (.all, .all): return true
        case (.local, .local): return true
        case (.cloud(let a), .cloud(let b)): return a == b
        default: return false
        }
    }
}
