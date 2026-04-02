import SwiftUI

struct SearchPopupView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            searchHeader
            Divider()
            filterBar
            Divider()
            resultsList
        }
        .frame(width: 700, height: 500)
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
            }
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
            let cloudVM = appState.cloudFileManager(for: accountId)
            Task {
                await cloudVM.navigateTo(parentPath)
                if !cloudItem.isDirectory {
                    cloudVM.selectedItemIDs = [cloudItem.id]
                }
            }
        }

        appState.activePanel = side
        dismiss()
    }

    private func triggerSearch() {
        let localDir = appState.activeFileManager.currentDirectory
        let accounts = appState.syncManager.accounts.map { (id: $0.id, name: $0.providerType.displayName) }
        appState.searchVM.performSearch(
            localDirectory: localDir,
            allAccounts: accounts
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

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: item.icon)
                .font(.system(size: 16))
                .foregroundStyle(item.isDirectory ? Color.accentColor : .secondary)
                .frame(width: 24, alignment: .center)

            VStack(alignment: .leading, spacing: 2) {
                Text(item.name)
                    .font(.system(size: 13))
                    .lineLimit(1)

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
