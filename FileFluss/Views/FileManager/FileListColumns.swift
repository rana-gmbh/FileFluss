import Foundation
import FileFlussCore
import AppKit

enum FileListColumnID: String, CaseIterable {
    case dateModified
    case dateCreated
    case size
    case kind

    var title: String {
        switch self {
        case .dateModified: return "Date Modified"
        case .dateCreated: return "Date Created"
        case .size: return "Size"
        case .kind: return "Kind"
        }
    }

    var sortKey: String {
        switch self {
        case .dateModified: return "date"
        case .dateCreated: return "dateCreated"
        case .size: return "size"
        case .kind: return "kind"
        }
    }

    static func from(sortKey: String) -> FileListColumnID? {
        FileListColumnID.allCases.first { $0.sortKey == sortKey }
    }
}

enum FileListColumnPrefs {
    private static let localKey = "FileList.local.visibleColumns"
    private static let cloudKey = "FileList.cloud.visibleColumns"
    private static let localNameAutoResizeKey = "FileList.local.nameAutoResize"
    private static let cloudNameAutoResizeKey = "FileList.cloud.nameAutoResize"
    private static let defaultIDs: Set<FileListColumnID> = [.dateModified, .size]

    /// When true, the Name column's minimum width is pinned to the
    /// longest visible filename + icon padding, so the user never has
    /// to scroll horizontally just to see the end of a filename.
    static func nameAutoResize(forCloud: Bool) -> Bool {
        UserDefaults.standard.bool(forKey: forCloud ? cloudNameAutoResizeKey : localNameAutoResizeKey)
    }

    static func setNameAutoResize(_ value: Bool, forCloud: Bool) {
        UserDefaults.standard.set(value, forKey: forCloud ? cloudNameAutoResizeKey : localNameAutoResizeKey)
        NotificationCenter.default.post(name: .fileListColumnsChanged, object: forCloud)
    }

    /// Cloud panels exclude Date Created — `CloudFileItem` does not carry a
    /// creation timestamp because most providers either don't expose one or
    /// expose it inconsistently.
    static func availableColumns(forCloud: Bool) -> [FileListColumnID] {
        forCloud
            ? [.dateModified, .size, .kind]
            : [.dateModified, .dateCreated, .size, .kind]
    }

    static func visibleColumns(forCloud: Bool) -> Set<FileListColumnID> {
        let key = forCloud ? cloudKey : localKey
        let available = Set(availableColumns(forCloud: forCloud))
        if let stored = UserDefaults.standard.array(forKey: key) as? [String] {
            return Set(stored.compactMap(FileListColumnID.init(rawValue:))).intersection(available)
        }
        return defaultIDs.intersection(available)
    }

    static func setVisibleColumns(_ ids: Set<FileListColumnID>, forCloud: Bool) {
        let key = forCloud ? cloudKey : localKey
        UserDefaults.standard.set(ids.map(\.rawValue), forKey: key)
        NotificationCenter.default.post(name: .fileListColumnsChanged, object: forCloud)
    }

    static func toggle(_ id: FileListColumnID, forCloud: Bool) {
        var current = visibleColumns(forCloud: forCloud)
        if current.contains(id) {
            current.remove(id)
        } else {
            current.insert(id)
        }
        setVisibleColumns(current, forCloud: forCloud)
    }
}

extension Notification.Name {
    static let fileListColumnsChanged = Notification.Name("fileListColumnsChanged")
}

// MARK: - Name-column elastic sizing
//
// When the enclosing panel shrinks (HSplitView drag, window resize, restore
// from minimize, app foreground), AppKit cannot shrink the Name column past
// its minWidth — so the row clips and the Name column scrolls out of view.
// We compute the available width and set the Name column directly so it stays
// visible at any pane size.
enum FileListNameColumn {
    /// Floor for the Name column so the title text stays readable at very
    /// narrow pane widths. Picked low enough that the column never overflows
    /// in realistic two-panel layouts.
    static let minWidth: CGFloat = 80

    /// Width that an icon + intercell padding contribute to a Name cell
    /// on top of the rendered text width — measured empirically against
    /// the local/cloud list row layout.
    static let iconAndPaddingWidth: CGFloat = 32

    /// Resize the Name column to fill whatever width is left after the
    /// other visible columns and intercell spacing. When `floor` is
    /// provided (Auto Resize preference is on, callers compute it from
    /// the longest visible filename), the column is never narrower
    /// than that — even if the resulting width exceeds the visible
    /// pane and forces horizontal scrolling.
    @MainActor
    static func resizeNameColumn(
        in tableView: NSTableView,
        nameColumnID: NSUserInterfaceItemIdentifier,
        floor floorOverride: CGFloat? = nil
    ) {
        guard let nameCol = tableView.tableColumns.first(where: { $0.identifier == nameColumnID }) else {
            return
        }
        let clipWidth = tableView.enclosingScrollView?.contentView.bounds.width ?? tableView.bounds.width
        guard clipWidth > 0 else { return }

        let visibleOthers = tableView.tableColumns.filter { $0.identifier != nameColumnID && !$0.isHidden }
        let othersWidth = visibleOthers.reduce(CGFloat(0)) { $0 + $1.width }
        // Gaps between the (N other + 1 name) visible columns = visibleOthers.count.
        let spacing = tableView.intercellSpacing.width * CGFloat(visibleOthers.count)

        let effectiveFloor = max(minWidth, floorOverride ?? 0)
        nameCol.minWidth = effectiveFloor
        let target = max(effectiveFloor, clipWidth - othersWidth - spacing)
        if abs(nameCol.width - target) > 0.5 {
            nameCol.width = target
        }
    }

    /// Measures the widest of `names` using the standard table-row
    /// font and returns text-width + icon/padding, suitable for use
    /// as the Name column's minimum width.
    static func widestRequiredWidth(forNames names: [String]) -> CGFloat {
        guard !names.isEmpty else { return minWidth }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: NSFont.systemFontSize)
        ]
        var widest: CGFloat = 0
        for name in names {
            let w = (name as NSString).size(withAttributes: attrs).width
            if w > widest { widest = w }
        }
        return widest + iconAndPaddingWidth
    }
}
