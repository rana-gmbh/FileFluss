import SwiftUI
import FileFlussCore

struct CompareFoldersView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    @State private var result: FolderComparisonResult?
    @State private var isCalculating: Bool = false
    @State private var errorMessage: String?
    @State private var filter: Filter = .differences
    @State private var compareDates: Bool = false
    @State private var leftEntries: [SyncEntry] = []
    @State private var rightEntries: [SyncEntry] = []
    /// Endpoints captured at the moment the comparison started. The view
    /// renders these (not live AppState) so navigating the panels while
    /// the compare window is open doesn't re-run the comparison or change
    /// the displayed paths.
    @State private var snapshotLeft: SyncEndpoint?
    @State private var snapshotRight: SyncEndpoint?

    enum Filter: String, CaseIterable, Hashable {
        case all = "All"
        case differences = "Differences"
        case onlyLeft = "Only Left"
        case onlyRight = "Only Right"
        case identical = "Identical"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            endpointsRow
            Divider()
            if isCalculating {
                loadingView
            } else if let errorMessage {
                errorView(errorMessage)
            } else if let result {
                resultsView(result)
            } else {
                placeholderView
            }
        }
        .padding(20)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .task(id: appState.compareTrigger) {
            await calculate()
        }
    }

    // MARK: - Header

    private var header: some View {
        HStack {
            LText("Compare Folders")
                .font(.title2).bold()
            Spacer()
            if let result, !isCalculating {
                Text(summaryText(for: result))
                    .font(.callout)
                    .foregroundStyle(.secondary)
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
            .help(L10n.text("Close (Esc)"))
            .keyboardShortcut(.cancelAction)
        }
    }

    private var endpointsRow: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 12) {
                endpointCard(title: L10n.text("Left"), endpoint: snapshotLeft)
                Image(systemName: "arrow.left.and.right")
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .padding(.top, 22)
                endpointCard(title: L10n.text("Right"), endpoint: snapshotRight)
            }
            HStack(spacing: 8) {
                Toggle(isOn: $compareDates) {
                    LText("Compare Date")
                        .font(.callout)
                }
                .toggleStyle(.checkbox)
                .help(L10n.text("Treat files with matching size but different modification dates as different. Shows which side is newer along with both modify and create dates."))
                .onChange(of: compareDates) { _, _ in
                    recompute()
                }
                Spacer()
                Button {
                    // Bump the trigger to re-snapshot from current panels
                    // and re-run the comparison. .task(id:) handles the rest.
                    appState.compareTrigger = UUID()
                } label: {
                    Label(L10n.text("Compare Again"), systemImage: "arrow.clockwise")
                        .font(.callout)
                }
                .help(L10n.text("Re-run the comparison against the panels' current folders"))
                .disabled(isCalculating)
            }
        }
    }

    private func endpointCard(title: String, endpoint: SyncEndpoint?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            if let endpoint {
                if case .cloud(let accountId, _) = endpoint,
                   let account = appState.syncManager.accounts.first(where: { $0.id == accountId }) {
                    HStack(spacing: 6) {
                        CloudProviderIcon(providerType: account.providerType, size: 14)
                        Text(account.displayName)
                            .font(.callout).bold()
                            .lineLimit(1)
                    }
                }
                HStack(spacing: 6) {
                    Image(systemName: "folder.fill")
                        .foregroundStyle(.secondary)
                    Text(endpoint.displayPath)
                        .font(.callout)
                        .lineLimit(2)
                        .truncationMode(.middle)
                }
            } else {
                LText("No folder open")
                    .foregroundStyle(.secondary)
                    .italic()
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
    }

    // MARK: - State Views

    private var loadingView: some View {
        VStack(spacing: 10) {
            ProgressView()
            LText("Reading folders…")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func errorView(_ msg: String) -> some View {
        VStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle")
                .font(.system(size: 36))
                .foregroundStyle(.orange)
            Text(msg)
                .font(.callout)
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
            Button(L10n.text("Try Again")) { Task { await calculate() } }
                .padding(.top, 6)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.horizontal, 24)
    }

    private var placeholderView: some View {
        ContentUnavailableView(
            L10n.text("Open a folder on each side to compare"),
            systemImage: "rectangle.split.2x1",
            description: Text(L10n.text("Both panels need a folder selected to run a comparison."))
        )
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func resultsView(_ result: FolderComparisonResult) -> some View {
        let tree = CompareTreeBuilder.build(from: result.entries)
        let visibleTopLevel = tree.children.filter { node in
            nodeMatchesCurrentFilter(node)
        }
        return VStack(spacing: 0) {
            filterChips(result: result)
                .padding(.bottom, 10)
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    if visibleTopLevel.isEmpty {
                        LText("Nothing matches this filter.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .padding(.vertical, 30)
                            .frame(maxWidth: .infinity)
                    } else {
                        ForEach(visibleTopLevel) { node in
                            nodeView(node, depth: 0)
                        }
                    }
                }
                .padding(.vertical, 4)
            }
            .background(.quaternary.opacity(0.25), in: RoundedRectangle(cornerRadius: 8))
        }
        .frame(maxHeight: .infinity)
    }

    /// True if `node` itself matches the active filter, or any descendant
    /// does (so folders containing matching items remain visible).
    private func nodeMatchesCurrentFilter(_ node: CompareTreeNode) -> Bool {
        if let entry = node.entry, entryMatchesFilter(entry) { return true }
        return node.children.contains { nodeMatchesCurrentFilter($0) }
    }

    private func entryMatchesFilter(_ entry: FolderCompareEntry) -> Bool {
        switch filter {
        case .all: return true
        case .differences: return entry.status != .identical
        case .onlyLeft: return entry.status == .onlyLeft
        case .onlyRight: return entry.status == .onlyRight
        case .identical: return entry.status == .identical
        }
    }

    /// Returns AnyView because the body recurses on itself, and SwiftUI's
    /// opaque-return-type inference would otherwise reject it.
    private func nodeView(_ node: CompareTreeNode, depth: Int) -> AnyView {
        let visibleChildren = node.children.filter { nodeMatchesCurrentFilter($0) }
        if node.isDirectory && !visibleChildren.isEmpty {
            return AnyView(
                DisclosureGroup {
                    ForEach(visibleChildren) { child in
                        nodeView(child, depth: depth + 1)
                    }
                } label: {
                    folderLabel(node)
                }
                .padding(.leading, CGFloat(depth) * 14 + 12)
                .padding(.trailing, 12)
                .padding(.vertical, 2)
            )
        } else {
            return AnyView(
                VStack(alignment: .leading, spacing: 0) {
                    entryRow(node: node, depth: depth)
                    if !node.isDirectory {
                        Divider().padding(.leading, CGFloat(depth) * 14 + 38)
                    }
                }
            )
        }
    }

    /// The clickable header label for a folder DisclosureGroup. Shows the
    /// folder's own status (or a neutral folder icon for synthetic nodes)
    /// and an aggregated count of what's hidden underneath.
    private func folderLabel(_ node: CompareTreeNode) -> some View {
        HStack(spacing: 8) {
            if let status = node.entry?.status {
                Image(systemName: statusSymbol(status))
                    .foregroundStyle(statusColor(status))
            } else {
                Image(systemName: "folder.fill")
                    .foregroundStyle(.secondary)
            }
            Text(node.name)
                .font(.callout)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer()
            Text(folderSummary(node))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .contentShape(Rectangle())
    }

    /// Counts of children matching the active filter, formatted for the
    /// right-hand column of a folder row.
    private func folderSummary(_ node: CompareTreeNode) -> String {
        let counts = node.descendantCounts()
        var parts: [String] = []
        switch filter {
        case .all:
            if counts.different > 0 { parts.append("\(counts.different) different") }
            if counts.onlyLeft > 0 { parts.append("\(counts.onlyLeft) only left") }
            if counts.onlyRight > 0 { parts.append("\(counts.onlyRight) only right") }
            if counts.identical > 0 { parts.append("\(counts.identical) same") }
        case .differences:
            if counts.different > 0 { parts.append("\(counts.different) different") }
            if counts.onlyLeft > 0 { parts.append("\(counts.onlyLeft) only left") }
            if counts.onlyRight > 0 { parts.append("\(counts.onlyRight) only right") }
        case .onlyLeft:
            if counts.onlyLeft > 0 { parts.append("\(counts.onlyLeft) only left") }
        case .onlyRight:
            if counts.onlyRight > 0 { parts.append("\(counts.onlyRight) only right") }
        case .identical:
            if counts.identical > 0 { parts.append("\(counts.identical) same") }
        }
        return parts.joined(separator: " · ")
    }

    /// Render a leaf row (file, or empty folder) for the tree.
    private func entryRow(node: CompareTreeNode, depth: Int) -> some View {
        let entry = node.entry
        return HStack(spacing: 10) {
            if let entry {
                Image(systemName: statusSymbol(entry.status))
                    .foregroundStyle(statusColor(entry.status))
                    .frame(width: 18)
            } else {
                Image(systemName: "folder.fill")
                    .foregroundStyle(.secondary)
                    .frame(width: 18)
            }

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Image(systemName: node.isDirectory ? "folder.fill" : "doc")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                    Text(node.name)
                        .font(.callout)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if let entry, compareDates, entry.status == .differs, let newer = newerSide(entry) {
                        Text(newer == .left ? "Left newer" : "Right newer")
                            .font(.caption2.bold())
                            .padding(.horizontal, 6)
                            .padding(.vertical, 1)
                            .background(.blue.opacity(0.15), in: Capsule())
                            .foregroundStyle(.blue)
                    }
                }
                if let entry, let detail = detailText(for: entry) {
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if let entry, compareDates, entry.status == .differs {
                    if let line = modifiedDateDetail(entry) {
                        Text(line)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if let line = createdDateDetail(entry) {
                        Text(line)
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                    }
                }
            }

            Spacer()
        }
        .padding(.leading, CGFloat(depth) * 14 + 12)
        .padding(.trailing, 12)
        .padding(.vertical, 6)
    }

    // MARK: - Result rendering

    private func filterChips(result: FolderComparisonResult) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(Filter.allCases, id: \.self) { f in
                    let count = count(for: f, in: result)
                    Button {
                        filter = f
                    } label: {
                        HStack(spacing: 4) {
                            Text(L10n.text(f.rawValue))
                            Text("(\(count))")
                                .foregroundStyle(.secondary)
                        }
                        .font(.callout)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 5)
                        .background(filter == f ? Color.accentColor.opacity(0.18) : Color.clear, in: Capsule())
                        .overlay(Capsule().stroke(filter == f ? Color.accentColor : .secondary.opacity(0.3)))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private enum Side { case left, right }

    private func newerSide(_ entry: FolderCompareEntry) -> Side? {
        guard let l = entry.leftDate, let r = entry.rightDate else { return nil }
        if abs(l.timeIntervalSince(r)) <= 2 { return nil }
        return l > r ? .left : .right
    }

    private static let detailDateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    private func formatDate(_ date: Date?) -> String {
        guard let date else { return "—" }
        return Self.detailDateFormatter.string(from: date)
    }

    private func modifiedDateDetail(_ entry: FolderCompareEntry) -> String? {
        guard entry.leftDate != nil || entry.rightDate != nil else { return nil }
        return L10n.format("Modified — Left: %@  •  Right: %@", formatDate(entry.leftDate), formatDate(entry.rightDate))
    }

    private func createdDateDetail(_ entry: FolderCompareEntry) -> String? {
        // Only render when at least one side has a creation date (cloud
        // listings don't carry one, so suppress the row in that case).
        guard entry.leftCreated != nil || entry.rightCreated != nil else { return nil }
        return L10n.format("Created  — Left: %@  •  Right: %@", formatDate(entry.leftCreated), formatDate(entry.rightCreated))
    }

    // MARK: - Helpers

    private func detailText(for entry: FolderCompareEntry) -> String? {
        if entry.isDirectory { return nil }
        switch entry.status {
        case .identical:
            if let s = entry.leftSize { return ByteCountFormatter.string(fromByteCount: s, countStyle: .file) }
            return nil
        case .onlyLeft:
            if let s = entry.leftSize { return L10n.format("Left: %@", ByteCountFormatter.string(fromByteCount: s, countStyle: .file)) }
            return nil
        case .onlyRight:
            if let s = entry.rightSize { return L10n.format("Right: %@", ByteCountFormatter.string(fromByteCount: s, countStyle: .file)) }
            return nil
        case .differs:
            if entry.leftSize == entry.rightSize, let s = entry.leftSize {
                return L10n.format("Same size: %@", ByteCountFormatter.string(fromByteCount: s, countStyle: .file))
            }
            let l = entry.leftSize.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } ?? "—"
            let r = entry.rightSize.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } ?? "—"
            return L10n.format("Left: %@ • Right: %@ — different size", l, r)
        }
    }

    private func statusSymbol(_ s: FolderCompareStatus) -> String {
        switch s {
        case .identical: return "checkmark.circle.fill"
        case .onlyLeft: return "arrow.left.circle.fill"
        case .onlyRight: return "arrow.right.circle.fill"
        case .differs: return "exclamationmark.triangle.fill"
        }
    }

    private func statusColor(_ s: FolderCompareStatus) -> Color {
        switch s {
        case .identical: return .green
        case .onlyLeft: return .blue
        case .onlyRight: return .orange
        case .differs: return .red
        }
    }

    private func count(for f: Filter, in result: FolderComparisonResult) -> Int {
        switch f {
        case .all: return result.entries.count
        case .differences:
            return result.differsCount + result.onlyLeftCount + result.onlyRightCount
        case .onlyLeft: return result.onlyLeftCount
        case .onlyRight: return result.onlyRightCount
        case .identical: return result.identicalCount
        }
    }

    private func summaryText(for result: FolderComparisonResult) -> String {
        let diff = result.differsCount
        let onlyL = result.onlyLeftCount
        let onlyR = result.onlyRightCount
        let same = result.identicalCount
        let total = diff + onlyL + onlyR
        if total == 0 {
            return L10n.format("Folders are identical (%d items)", same)
        }
        return L10n.format("%d different • %d only-left • %d only-right • %d same", diff, onlyL, onlyR, same)
    }

    // MARK: - Endpoints / calculation

    private func currentEndpoint(for side: PanelSide) -> SyncEndpoint? {
        let item = appState.sidebarSelection(for: side)
        switch item {
        case .cloudAccount(let account):
            if account.isConnected {
                let vm = appState.cloudFileManager(for: account.id, side: side)
                return .cloud(accountId: account.id, rootPath: vm.currentPath)
            }
            return .offlineIndexed(
                sourceId: account.id.uuidString,
                kind: .cloud(accountId: account.id),
                rootPath: account.rootPath.isEmpty ? "/" : account.rootPath,
                displayName: account.displayName
            )
        case .cloudFolder(let accountId, let path):
            if let account = appState.syncManager.accountFor(id: accountId), account.isConnected {
                let vm = appState.cloudFileManager(for: accountId, side: side)
                return .cloud(accountId: accountId, rootPath: vm.currentPath)
            }
            let name = appState.syncManager.accountFor(id: accountId)?.displayName ?? "Cloud Account"
            return .offlineIndexed(
                sourceId: accountId.uuidString,
                kind: .cloud(accountId: accountId),
                rootPath: path.isEmpty ? "/" : path,
                displayName: name
            )
        case .drive(let driveId):
            if let mount = appState.driveMonitor.mountURL(for: driveId) {
                return .local(mount)
            }
            let name = appState.driveMonitor.drives.first(where: { $0.id == driveId })?.displayName ?? "Drive"
            return .offlineIndexed(
                sourceId: driveId,
                kind: .drive,
                rootPath: "/",
                displayName: name
            )
        case .offlineFolder(let sourceId, let path):
            // Could be a drive id ("vol:…", "net:…") or a cloud account
            // UUID string. Tell them apart by trying to parse a UUID.
            if let uuid = UUID(uuidString: sourceId) {
                let name = appState.syncManager.accountFor(id: uuid)?.displayName ?? "Cloud Account"
                return .offlineIndexed(
                    sourceId: sourceId,
                    kind: .cloud(accountId: uuid),
                    rootPath: path,
                    displayName: name
                )
            } else {
                let name = appState.driveMonitor.drives.first(where: { $0.id == sourceId })?.displayName ?? "Drive"
                return .offlineIndexed(
                    sourceId: sourceId,
                    kind: .drive,
                    rootPath: path,
                    displayName: name
                )
            }
        default:
            return .local(appState.fileManager(for: side).currentDirectory)
        }
    }

    private func calculate() async {
        // Snapshot the endpoints right now so subsequent panel navigation
        // doesn't change what we're comparing or what's displayed.
        let left = currentEndpoint(for: .left)
        let right = currentEndpoint(for: .right)
        snapshotLeft = left
        snapshotRight = right

        guard let left, let right else {
            result = nil
            return
        }
        isCalculating = true
        errorMessage = nil
        defer { isCalculating = false }

        let planner = SyncPlanner()
        do {
            async let leftTask = planner.enumerate(left)
            async let rightTask = planner.enumerate(right)
            let (l, r) = try await (leftTask, rightTask)
            leftEntries = l
            rightEntries = r
            result = FolderComparison.compare(left: l, right: r, compareDates: compareDates)
        } catch {
            errorMessage = "Could not read folders: \(error.localizedDescription)"
            result = nil
        }
    }

    /// Re-runs the diff against the cached enumerated entries. Used when
    /// the user toggles `Compare Date` so we don't re-scan the cloud.
    private func recompute() {
        guard !leftEntries.isEmpty || !rightEntries.isEmpty else { return }
        result = FolderComparison.compare(
            left: leftEntries,
            right: rightEntries,
            compareDates: compareDates
        )
    }
}
