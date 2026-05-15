import SwiftUI
import FileFlussCore

/// Read-only browser shown when the user navigates to an offline indexed
/// source — an unmounted external drive, a disconnected network mount, or
/// an offline cloud account. Lists folder children from `SearchIndex` and
/// supports drill-down via the parent-path column.
///
/// Visual cues for offline state:
/// - Filenames rendered in italic with secondary foreground.
/// - A persistent banner across the top makes the offline state obvious.
/// - File actions (open, preview, drag-out) are disabled — the user can
///   only navigate and copy the path for reference.
struct OfflineSourceView: View {
    let sourceId: String
    let panelSide: PanelSide
    /// Initial folder to display. Defaults to "/" — the source root.
    var initialPath: String = "/"
    /// Display name when no IndexedSource record exists (e.g. just clicked
    /// from a stale search result).
    var fallbackTitle: String?

    @Environment(AppState.self) private var appState
    @State private var path: String = "/"
    @State private var rows: [SearchIndex.IndexedFile] = []
    @State private var source: SearchIndex.IndexedSource?
    @State private var pathStack: [String] = []
    @State private var isLoading = false

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    var body: some View {
        VStack(spacing: 0) {
            offlineBanner
            pathBar
            Divider()
            if rows.isEmpty && !isLoading {
                emptyState
            } else {
                list
            }
        }
        .task(id: "\(sourceId):\(path)") {
            await load()
        }
        .onAppear {
            path = initialPath
        }
    }

    private var displayName: String {
        if let name = source?.displayName { return name }
        if let drive = appState.driveMonitor.drives.first(where: { $0.id == sourceId }) {
            return drive.displayName
        }
        if let uuid = UUID(uuidString: sourceId),
           let account = appState.syncManager.accountFor(id: uuid) {
            return account.displayName
        }
        return fallbackTitle ?? "Offline Source"
    }

    private var lastIndexedText: String {
        guard let date = source?.lastIndexed else { return "Never indexed" }
        let interval = Date().timeIntervalSince(date)
        let days = Int(interval / 86_400)
        if days == 0 { return "Indexed today" }
        if days == 1 { return "Indexed 1 day ago" }
        return "Indexed \(days) days ago"
    }

    private var offlineBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "wifi.slash")
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 0) {
                Text("Offline view — \(displayName)")
                    .font(.callout.weight(.medium))
                Text(lastIndexedText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Text("Read only")
                .font(.caption)
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .background(Color.secondary.opacity(0.18), in: Capsule())
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.thinMaterial)
    }

    private var pathBar: some View {
        HStack(spacing: 6) {
            Button {
                goUp()
            } label: {
                Image(systemName: "chevron.left")
            }
            .buttonStyle(.borderless)
            .disabled(pathStack.isEmpty)
            .help("Go back")

            Text(path)
                .font(.callout)
                .lineLimit(1)
                .truncationMode(.head)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            Spacer()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "tray")
                .font(.system(size: 32))
                .foregroundStyle(.tertiary)
            Text("No indexed contents")
                .foregroundStyle(.secondary)
            Text("Re-index this source while it's online to populate offline contents.")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 30)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var list: some View {
        Table(rows) {
            TableColumn("Name") { (item: SearchIndex.IndexedFile) in
                HStack(spacing: 8) {
                    Image(systemName: item.isDirectory ? "folder.fill" : itemIcon(for: item.name))
                        .foregroundStyle(.secondary)
                    Text(item.name)
                        .italic()
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                .contentShape(Rectangle())
                .onTapGesture(count: 2) {
                    if item.isDirectory { drillInto(item.path) }
                }
                .contextMenu {
                    Button("Copy Path") {
                        let pb = NSPasteboard.general
                        pb.clearContents()
                        pb.setString(item.path, forType: .string)
                    }
                }
            }
            .width(min: 200, ideal: 360)
            TableColumn("Size") { (item: SearchIndex.IndexedFile) in
                Text(item.isDirectory ? "--" : ByteCountFormatter.string(fromByteCount: item.size, countStyle: .file))
                    .foregroundStyle(.secondary)
            }
            .width(min: 70, ideal: 90)
            TableColumn("Modified") { (item: SearchIndex.IndexedFile) in
                Text(Self.dateFormatter.string(from: item.modificationDate))
                    .foregroundStyle(.secondary)
            }
            .width(min: 120, ideal: 160)
        }
        .opacity(0.92)
    }

    private func goUp() {
        guard !pathStack.isEmpty else { return }
        path = pathStack.removeLast()
    }

    private func drillInto(_ newPath: String) {
        pathStack.append(path)
        path = newPath
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }
        let target = path
        let id = sourceId
        // Cloud accounts cache their entries in `cloud_files` keyed by
        // `account_id`, drives use `indexed_files` keyed by `source_id`.
        // The convention is that drive sourceIds carry a `vol:` prefix;
        // anything else with a valid UUID is a cloud account.
        let loaded: [SearchIndex.IndexedFile]
        if let accountUUID = UUID(uuidString: id) {
            loaded = await SearchIndex.shared.listCloudChildren(accountId: accountUUID, parentPath: target)
        } else {
            loaded = await SearchIndex.shared.listChildren(sourceId: id, parentPath: target)
        }
        let meta = await SearchIndex.shared.source(id)
        if path == target && sourceId == id {
            rows = loaded
            source = meta
        }
    }

    private func itemIcon(for name: String) -> String {
        let ext = (name as NSString).pathExtension.lowercased()
        switch ext {
        case "jpg", "jpeg", "png", "gif", "heic", "webp", "tiff": return "photo"
        case "mp4", "mov", "avi", "mkv": return "film"
        case "mp3", "aac", "wav", "flac", "m4a": return "music.note"
        case "pdf": return "doc.richtext"
        case "zip", "tar", "gz", "rar", "7z": return "doc.zipper"
        case "txt", "md", "rtf": return "doc.text"
        default: return "doc"
        }
    }
}
