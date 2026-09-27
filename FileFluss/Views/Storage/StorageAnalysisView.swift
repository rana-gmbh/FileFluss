import SwiftUI
import FileFlussCore

/// "Where has my space gone" for a cloud account, a folder inside one, or a
/// local folder: a size-ordered folder tree beside a list of the biggest
/// individual files.
struct StorageAnalysisView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    @State private var scanner = StorageScanner()
    @State private var selectedSourceID: String?
    @State private var expanded: Set<String> = []
    /// Hides everything below this size, so a long tail of small files
    /// doesn't bury the few entries that actually matter.
    @State private var minimumSize: Int64 = 0

    private static let sizeThresholds: [(label: String, bytes: Int64)] = [
        ("All sizes", 0),
        ("1 MB and up", 1_000_000),
        ("10 MB and up", 10_000_000),
        ("100 MB and up", 100_000_000),
        ("1 GB and up", 1_000_000_000),
    ]

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            sourceBar
            Divider()

            if let message = scanner.errorMessage {
                errorView(message)
            } else if let report = scanner.report {
                resultsView(report)
            } else if scanner.isScanning {
                scanningPlaceholder
            } else {
                placeholder
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear {
            if selectedSourceID == nil {
                selectedSourceID = sources.first?.id
            }
        }
    }

    // MARK: - Header

    private var header: some View {
        HStack(spacing: 12) {
            LText("Storage Analysis")
                .font(.title2).bold()

            if scanner.isScanning {
                ProgressView()
                    .controlSize(.small)
                Text(progressText)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Button(L10n.text("Cancel")) { scanner.cancel() }
                    .controlSize(.small)
            } else if let report = scanner.report {
                Text(summary(report))
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            Spacer()

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
            .help(L10n.text("Close (Esc)"))
            .keyboardShortcut(.cancelAction)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 14)
    }

    // MARK: - Source picker

    private var sourceBar: some View {
        HStack(spacing: 10) {
            Picker(L10n.text("Analyse"), selection: $selectedSourceID) {
                ForEach(sources) { source in
                    Text(source.title).tag(Optional(source.id))
                }
            }
            .labelsHidden()
            .frame(maxWidth: 320)

            Button(L10n.text("Choose Folder…")) { chooseLocalFolder() }
                .help(L10n.text("Analyse any folder on this Mac"))

            Spacer()

            Picker(L10n.text("Show"), selection: $minimumSize) {
                ForEach(Self.sizeThresholds, id: \.bytes) { threshold in
                    Text(L10n.text(threshold.label)).tag(threshold.bytes)
                }
            }
            .labelsHidden()
            .frame(width: 150)

            if scanner.report?.fromIndex == true {
                Button(L10n.text("Rescan")) { startScan(force: true) }
                    .help(L10n.text("Read the account again instead of using the offline index"))
            }
            Button(L10n.text("Analyse")) { startScan(force: false) }
                .keyboardShortcut(.defaultAction)
                .disabled(selectedSource == nil || scanner.isScanning)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
    }

    // MARK: - Results

    private func resultsView(_ report: StorageReport) -> some View {
        VStack(spacing: 0) {
            if report.fromIndex, let indexedAt = report.indexedAt {
                banner(
                    icon: "clock.arrow.circlepath",
                    text: L10n.format(
                        "From the offline index of %@ — anything changed since then isn't reflected. Use Rescan for current figures.",
                        indexedAt.formatted(date: .abbreviated, time: .shortened)
                    )
                )
            }
            if report.wasCancelled {
                banner(
                    icon: "exclamationmark.triangle.fill",
                    text: L10n.text("Stopped early, so these totals are a lower bound — the real figures are larger.")
                )
            }

            HSplitView {
                treeColumn(report)
                    .frame(minWidth: 320)
                largestFilesColumn(report)
                    .frame(minWidth: 260)
            }
        }
    }

    private func treeColumn(_ report: StorageReport) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            columnTitle(L10n.text("Folders"))
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(visibleRows(report.root), id: \.node.id) { row in
                        StorageRow(
                            node: row.node,
                            total: report.root.size,
                            depth: row.depth,
                            isExpanded: expanded.contains(row.node.id),
                            toggle: { toggle(row.node) },
                            reveal: { reveal(row.node) }
                        )
                    }
                }
                .padding(.vertical, 4)
            }
        }
    }

    private struct VisibleRow {
        let node: StorageNode
        let depth: Int
    }

    /// Flattens the expanded part of the tree into a list. A recursive view
    /// can't infer its own type, and a flat list also means only what is
    /// open costs anything — a hundred-thousand-file account stays
    /// scrollable.
    private func visibleRows(_ root: StorageNode) -> [VisibleRow] {
        var rows: [VisibleRow] = []
        func append(_ node: StorageNode, depth: Int) {
            rows.append(VisibleRow(node: node, depth: depth))
            guard expanded.contains(node.id) else { return }
            for child in node.children where child.isDirectory || child.size >= minimumSize {
                append(child, depth: depth + 1)
            }
        }
        append(root, depth: 0)
        return rows
    }

    private func largestFilesColumn(_ report: StorageReport) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            columnTitle(L10n.text("Largest files"))
            let files = report.largestFiles.filter { $0.size >= minimumSize }
            if files.isEmpty {
                LText("No files above this size.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .padding(20)
                Spacer()
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(files) { file in
                            LargeFileRow(
                                file: file,
                                total: report.root.size,
                                reveal: { reveal(file) }
                            )
                        }
                    }
                    .padding(.vertical, 4)
                }
            }
        }
    }

    private func columnTitle(_ text: String) -> some View {
        Text(text)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.4))
    }

    private func banner(icon: String, text: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: icon)
                .foregroundStyle(.secondary)
            Text(text)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 8)
        .background(.quaternary.opacity(0.3))
    }

    private var placeholder: some View {
        VStack(spacing: 10) {
            Image(systemName: "chart.pie")
                .font(.system(size: 34))
                .foregroundStyle(.secondary)
            LText("Pick a cloud account or a folder, then choose Analyse.")
                .foregroundStyle(.secondary)
            LText("An account that was indexed for offline search is shown instantly, without any network requests.")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 380)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var scanningPlaceholder: some View {
        VStack(spacing: 10) {
            ProgressView()
            Text(progressText)
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func errorView(_ message: String) -> some View {
        VStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle")
                .font(.system(size: 30))
                .foregroundStyle(.orange)
            Text(message)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 380)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: - Data

    private var sources: [StorageScanSource] {
        var result: [StorageScanSource] = []
        for account in appState.syncManager.accounts where account.isConnected {
            result.append(.cloud(accountId: account.id, rootPath: "/", displayName: account.displayName))
        }
        // The folder each panel is showing, so "analyse what I'm looking at"
        // needs no folder picker.
        for side in [PanelSide.left, PanelSide.right] {
            if let accountId = appState.cloudAccountId(for: side) {
                let vm = appState.cloudFileManager(for: accountId, side: side)
                if vm.currentPath != "/", let account = appState.syncManager.accountFor(id: accountId) {
                    result.append(.cloud(
                        accountId: accountId,
                        rootPath: vm.currentPath,
                        displayName: account.displayName
                    ))
                }
            } else {
                result.append(.local(url: appState.fileManager(for: side).currentDirectory))
            }
        }
        // Keep the picker free of duplicates when both panels agree.
        var seen: Set<String> = []
        return result.filter { seen.insert($0.id).inserted }
    }

    private var selectedSource: StorageScanSource? {
        sources.first { $0.id == selectedSourceID } ?? extraLocalSource
    }

    @State private var extraLocalSource: StorageScanSource?

    private var progressText: String {
        let bytes = ByteCountFormatter.string(fromByteCount: scanner.bytesSeen, countStyle: .file)
        if scanner.currentPath.isEmpty {
            return L10n.format("%d files · %@", scanner.filesSeen, bytes)
        }
        return L10n.format("%d files · %@ · %@", scanner.filesSeen, bytes, scanner.currentPath)
    }

    private func summary(_ report: StorageReport) -> String {
        let bytes = ByteCountFormatter.string(fromByteCount: report.totalBytes, countStyle: .file)
        return L10n.format("%@ · %d files · %d folders", bytes, report.fileCount, report.folderCount)
    }

    // MARK: - Actions

    private func startScan(force: Bool) {
        guard let source = selectedSource else { return }
        expanded = []
        scanner.scan(source, forceRescan: force)
        // Open the top level straight away; a collapsed root tells nobody
        // anything.
        Task {
            try? await Task.sleep(for: .milliseconds(150))
            if let root = scanner.report?.root { expanded.insert(root.id) }
        }
    }

    private func chooseLocalFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = L10n.text("Analyse")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let source = StorageScanSource.local(url: url)
        extraLocalSource = source
        selectedSourceID = source.id
        scanner.scan(source)
        Task {
            try? await Task.sleep(for: .milliseconds(150))
            if let root = scanner.report?.root { expanded.insert(root.id) }
        }
    }

    private func toggle(_ node: StorageNode) {
        guard node.isDirectory else { return }
        if expanded.contains(node.id) {
            expanded.remove(node.id)
        } else {
            expanded.insert(node.id)
        }
    }

    /// Shows the item where the user can act on it: a local path in Finder,
    /// a cloud path in the active panel.
    private func reveal(_ node: StorageNode) {
        switch scanner.scannedSource {
        case .local:
            NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: node.path)])
        case .cloud(let accountId, _, _):
            let side = appState.activePanel
            let target = node.isDirectory ? node.path : (node.path as NSString).deletingLastPathComponent
            guard let account = appState.syncManager.accountFor(id: accountId) else { return }
            appState.setSidebarSelection(.cloudAccount(account), for: side)
            Task {
                let vm = appState.cloudFileManager(for: accountId, side: side)
                await vm.navigateTo(target)
            }
        case .none:
            break
        }
    }
}

/// One folder-tree row: indent, disclosure arrow, name, size bar, size.
private struct StorageRow: View {
    let node: StorageNode
    let total: Int64
    let depth: Int
    let isExpanded: Bool
    let toggle: () -> Void
    let reveal: () -> Void

    var body: some View {
        HStack(spacing: 6) {
            Color.clear.frame(width: CGFloat(depth) * 14, height: 1)

            if node.isDirectory && !node.children.isEmpty {
                Button(action: toggle) {
                    Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .frame(width: 12)
                }
                .buttonStyle(.plain)
            } else {
                Color.clear.frame(width: 12, height: 1)
            }

            Image(systemName: node.isDirectory ? "folder.fill" : "doc")
                .foregroundStyle(node.isDirectory ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                .font(.system(size: 11))

            Text(node.name)
                .lineLimit(1)
                .truncationMode(.middle)

            Spacer(minLength: 8)

            shareBar
            Text(ByteCountFormatter.string(fromByteCount: node.size, countStyle: .file))
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 78, alignment: .trailing)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 3)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { reveal() }
        .onTapGesture { toggle() }
        .contextMenu {
            Button(L10n.text("Reveal")) { reveal() }
            Button(L10n.text("Copy Path")) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(node.path, forType: .string)
            }
        }
    }

    private var shareBar: some View {
        let fraction = node.fraction(of: total)
        return ZStack(alignment: .leading) {
            RoundedRectangle(cornerRadius: 2)
                .fill(.quaternary)
            RoundedRectangle(cornerRadius: 2)
                .fill(.tint)
                .frame(width: max(2, 60 * fraction))
        }
        .frame(width: 60, height: 6)
        .help(L10n.format("%.1f%% of the total", fraction * 100))
    }
}

private struct LargeFileRow: View {
    let file: StorageNode
    let total: Int64
    let reveal: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(file.name)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text((file.path as NSString).deletingLastPathComponent)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 8)
            Text(ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { reveal() }
        .contextMenu {
            Button(L10n.text("Reveal")) { reveal() }
            Button(L10n.text("Copy Path")) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(file.path, forType: .string)
            }
        }
    }
}
