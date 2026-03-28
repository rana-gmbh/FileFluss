import SwiftUI

struct FileListView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        VStack(spacing: 0) {
            pathBar
            Divider()
            fileTable
        }
    }

    private var pathBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                ForEach(pathComponents, id: \.url) { component in
                    Button(component.name) {
                        Task { await appState.fileManager.navigateTo(component.url) }
                    }
                    .buttonStyle(.plain)
                    .font(.system(.body, design: .default, weight: .medium))

                    if component.url != appState.fileManager.currentDirectory {
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

    private var fileTable: some View {
        Group {
            if appState.fileManager.isLoading {
                ProgressView()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if appState.fileManager.filteredItems.isEmpty {
                ContentUnavailableView("No Files", systemImage: "folder", description: Text("This folder is empty"))
            } else {
                Table(appState.fileManager.filteredItems, selection: Bindable(appState.fileManager).selectedItemIDs) {
                    TableColumn("Name") { item in
                        FileRowView(item: item)
                    }
                    .width(min: 200)

                    TableColumn("Date Modified") { item in
                        Text(item.formattedDate)
                            .foregroundStyle(.secondary)
                    }
                    .width(min: 120, ideal: 160)

                    TableColumn("Size") { item in
                        Text(item.formattedSize)
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                    .width(min: 60, ideal: 80)
                }
                .contextMenu(forSelectionType: String.self) { ids in
                    fileContextMenu(for: ids)
                } primaryAction: { ids in
                    guard let id = ids.first,
                          let item = appState.fileManager.items.first(where: { $0.id == id }) else { return }
                    Task { await appState.fileManager.openItem(item) }
                }
            }
        }
    }

    @ViewBuilder
    private func fileContextMenu(for ids: Set<String>) -> some View {
        Button("Open") {
            let matchingItems = appState.fileManager.items.filter { ids.contains($0.id) }
            for item in matchingItems {
                Task { await appState.fileManager.openItem(item) }
            }
        }
        Divider()
        Button("Move to Trash", role: .destructive) {
            Task { await appState.fileManager.deleteSelectedItems() }
        }
    }

    private var pathComponents: [(name: String, url: URL)] {
        var components: [(String, URL)] = []
        var url = appState.fileManager.currentDirectory
        while url.path() != "/" {
            components.insert((url.lastPathComponent, url), at: 0)
            url = url.deletingLastPathComponent()
        }
        components.insert(("/", URL(filePath: "/")), at: 0)
        return components
    }
}
