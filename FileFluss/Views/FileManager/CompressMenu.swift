import AppKit
import FileFlussCore

/// What a Compress menu item asks for: the selection and the format.
/// Rides in `NSMenuItem.representedObject`, which wants an object.
final class CompressRequest: NSObject {
    /// `[FileItem]` from a local panel, `[CloudFileItem]` from a cloud one.
    let items: Any
    let format: ArchiveCreationFormat

    init(items: Any, format: ArchiveCreationFormat) {
        self.items = items
        self.format = format
    }
}

/// The "Compress" submenu, shared by the local and cloud panels so both offer
/// the same formats in the same order.
@MainActor
enum CompressMenu {
    static func item(
        itemCount: Int, payload: Any, target: AnyObject, action: Selector
    ) -> NSMenuItem {
        let title = itemCount == 1
            ? L10n.text("Compress")
            : L10n.format("Compress %d Items", itemCount)
        let parent = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let submenu = NSMenu(title: title)

        for format in ArchiveCreationFormat.allCases {
            // RAR sits below a separator: it is the one format that depends
            // on something the user has to install.
            if format == .rar { submenu.addItem(.separator()) }

            let entry: NSMenuItem
            if format.isAvailable {
                entry = NSMenuItem(title: format.displayName, action: action, keyEquivalent: "")
                entry.target = target
                entry.representedObject = CompressRequest(items: payload, format: format)
            } else {
                // Shown rather than hidden, and clickable: it offers to fetch
                // the one thing RAR needs.
                entry = NSMenuItem(
                    title: L10n.text("RAR — Install rar Tool…"),
                    action: #selector(RarInstallRequester.request(_:)), keyEquivalent: ""
                )
                entry.target = RarInstallRequester.shared
                entry.toolTip = L10n.text(
                    "RAR archives can only be made with RARLAB's rar tool. FileFluss can download it for you."
                )
            }
            submenu.addItem(entry)
        }
        parent.submenu = submenu
        return parent
    }
}

extension Notification.Name {
    /// Asks the main window to offer installing RARLAB's rar tool.
    static let requestRarInstall = Notification.Name("FileFlussRequestRarInstall")
}

/// Target for the menu entry, which has no panel-specific work to do — the
/// offer is shown once, by the main window.
@MainActor
final class RarInstallRequester: NSObject {
    static let shared = RarInstallRequester()

    @objc func request(_ sender: Any?) {
        NotificationCenter.default.post(name: .requestRarInstall, object: nil)
    }
}
