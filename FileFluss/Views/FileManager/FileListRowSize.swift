import Foundation
import AppKit

/// Two-step row scaling for the file panels, modelled on Finder's
/// list-view Cmd+= / Cmd+- behaviour. Only the row content's text size
/// and the row height change — column headers, the sidebar, toolbar,
/// status bar, and row icons all stay at their default size so the
/// surrounding layout doesn't shift.
enum FileListRowSize: String, CaseIterable {
    case regular
    case large

    /// Row height in points. Tall enough to comfortably fit the
    /// configured text size with a touch of vertical breathing room.
    var rowHeight: CGFloat {
        switch self {
        case .regular: return 24
        case .large:   return 32
        }
    }

    /// Point size for the row text labels.
    var fontSize: CGFloat {
        switch self {
        case .regular: return NSFont.systemFontSize
        case .large:   return NSFont.systemFontSize + 3
        }
    }

    var systemFont: NSFont {
        .systemFont(ofSize: fontSize)
    }

    var monospacedDigitFont: NSFont {
        .monospacedDigitSystemFont(ofSize: fontSize, weight: .regular)
    }
}

enum FileListRowSizePrefs {
    static let storageKey = "FileList.rowSize"

    static var current: FileListRowSize {
        let raw = UserDefaults.standard.string(forKey: storageKey) ?? FileListRowSize.regular.rawValue
        return FileListRowSize(rawValue: raw) ?? .regular
    }

    static func set(_ size: FileListRowSize) {
        UserDefaults.standard.set(size.rawValue, forKey: storageKey)
        NotificationCenter.default.post(name: .fileListRowSizeChanged, object: nil)
    }

    /// Bumps the size up to `.large` when at `.regular`; otherwise no-op.
    static func increase() {
        if current == .regular { set(.large) }
    }

    /// Bumps the size down to `.regular` when at `.large`; otherwise no-op.
    static func decrease() {
        if current == .large { set(.regular) }
    }
}

extension Notification.Name {
    /// Posted when the user toggles the row-size pref (Cmd+= / Cmd+-).
    /// Both native file-list coordinators listen and re-apply the new
    /// row height + cell font.
    static let fileListRowSizeChanged = Notification.Name("fileListRowSizeChanged")
}
