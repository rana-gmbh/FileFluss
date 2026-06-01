import SwiftUI
import FileFlussCore
import AppKit
import UniformTypeIdentifiers
import QuickLookUI

// MARK: - NSViewRepresentable Bridge

struct NativeFileList: NSViewRepresentable {
    let items: [FileItem]
    let currentDirectory: URL
    let panelSide: PanelSide
    var hideFileExtensions: Bool = false
    @Binding var selectedIDs: Set<String>
    var quickLookController: QuickLookController?
    var onDoubleClick: (FileItem) -> Void
    var onDrop: ([FileItem], URL) -> Void
    var onKeySpace: () -> Void
    var onDelete: (() -> Void)?
    var onCopyToOtherPanel: (([FileItem]) -> Void)?
    var onMoveToOtherPanel: (([FileItem]) -> Void)?
    var onCalculateFolderSize: ((FileItem) -> Void)?
    var onAddToFavorites: ((FileItem) -> Void)?
    var onSortChanged: ((String, Bool) -> Void)?
    var onBecameActive: (() -> Void)?
    var onReceivePromises: ((URL) -> Void)?
    var onCreateFolder: (() -> Void)?
    var onRename: ((FileItem) -> Void)?

    func makeCoordinator() -> FileTableCoordinator {
        FileTableCoordinator()
    }

    func makeNSView(context: Context) -> NSScrollView {
        let coordinator = context.coordinator

        let tableView = FileTableView()
        tableView.style = .inset
        tableView.usesAlternatingRowBackgroundColors = true
        tableView.allowsMultipleSelection = true
        tableView.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        tableView.rowHeight = FileListRowSizePrefs.current.rowHeight
        tableView.intercellSpacing = NSSize(width: 10, height: 4)
        tableView.headerView = NSTableHeaderView()
        tableView.gridStyleMask = []

        // Name column (always visible)
        let nameCol = NSTableColumn(identifier: .nameColumn)
        nameCol.title = L10n.text("Name")
        nameCol.minWidth = FileListNameColumn.minWidth
        nameCol.sortDescriptorPrototype = NSSortDescriptor(key: "name", ascending: true, selector: #selector(NSString.localizedStandardCompare(_:)))
        tableView.addTableColumn(nameCol)

        // Date Modified
        let dateCol = NSTableColumn(identifier: .dateColumn)
        dateCol.title = L10n.text("Date Modified")
        dateCol.width = 160
        dateCol.minWidth = 100
        dateCol.sortDescriptorPrototype = NSSortDescriptor(key: "date", ascending: true)
        tableView.addTableColumn(dateCol)

        // Date Created
        let dateCreatedCol = NSTableColumn(identifier: .dateCreatedColumn)
        dateCreatedCol.title = L10n.text("Date Created")
        dateCreatedCol.width = 160
        dateCreatedCol.minWidth = 100
        dateCreatedCol.sortDescriptorPrototype = NSSortDescriptor(key: "dateCreated", ascending: true)
        tableView.addTableColumn(dateCreatedCol)

        // Size
        let sizeCol = NSTableColumn(identifier: .sizeColumn)
        sizeCol.title = L10n.text("Size")
        sizeCol.width = 80
        sizeCol.minWidth = 60
        sizeCol.sortDescriptorPrototype = NSSortDescriptor(key: "size", ascending: true)
        tableView.addTableColumn(sizeCol)

        // Kind
        let kindCol = NSTableColumn(identifier: .kindColumn)
        kindCol.title = L10n.text("Kind")
        kindCol.width = 120
        kindCol.minWidth = 80
        kindCol.sortDescriptorPrototype = NSSortDescriptor(key: "kind", ascending: true, selector: #selector(NSString.localizedStandardCompare(_:)))
        tableView.addTableColumn(kindCol)

        coordinator.applyColumnVisibility(to: tableView)

        // Header context menu — right-click on a column header to toggle visibility
        let headerMenu = NSMenu()
        headerMenu.delegate = coordinator
        coordinator.headerMenu = headerMenu
        tableView.headerView?.menu = headerMenu

        // The Name column stretches to fill
        tableView.sizeLastColumnToFit()

        // Data source & delegate
        tableView.dataSource = coordinator
        tableView.delegate = coordinator

        // Double-click
        tableView.doubleAction = #selector(coordinator.handleDoubleClick(_:))
        tableView.target = coordinator

        // Register for drop (file URLs + file promises from cloud panels)
        let promiseTypes = NSFilePromiseReceiver.readableDraggedTypes.map { NSPasteboard.PasteboardType($0) }
        tableView.registerForDraggedTypes([.fileURL] + promiseTypes)
        tableView.setDraggingSourceOperationMask(.every, forLocal: true)
        tableView.setDraggingSourceOperationMask(.every, forLocal: false)

        // Space key callback
        tableView.onSpaceKey = { [weak coordinator] in
            coordinator?.onKeySpace?()
        }

        // Delete callback (Cmd+Delete)
        tableView.onDelete = { [weak coordinator] in
            coordinator?.onDelete?()
        }

        // Became active callback (first responder)
        tableView.onBecameFirstResponder = { [weak coordinator] in
            coordinator?.onBecameActive?()
        }

        // Quick Look controller
        if let qlController = quickLookController {
            tableView.quickLookController = qlController
            qlController.sourceTableView = tableView
        }

        // Context menu
        let menu = NSMenu()
        menu.delegate = coordinator
        tableView.menu = menu

        // Scroll view
        let scrollView = NSScrollView()
        scrollView.documentView = tableView
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = true

        coordinator.tableView = tableView
        coordinator.installNameColumnResizing(scrollView: scrollView)

        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        let coordinator = context.coordinator
        let tableView = coordinator.tableView!

        // Update callbacks
        coordinator.currentDirectory = currentDirectory
        coordinator.panelSide = panelSide
        coordinator.onDoubleClick = onDoubleClick
        coordinator.onDrop = onDrop
        coordinator.onKeySpace = onKeySpace
        coordinator.onDelete = onDelete
        coordinator.onCopyToOtherPanel = onCopyToOtherPanel
        coordinator.onMoveToOtherPanel = onMoveToOtherPanel
        coordinator.onCalculateFolderSize = onCalculateFolderSize
        coordinator.onAddToFavorites = onAddToFavorites
        coordinator.onSortChanged = onSortChanged
        coordinator.onBecameActive = onBecameActive
        coordinator.onReceivePromises = onReceivePromises
        coordinator.onCreateFolder = onCreateFolder
        coordinator.onRename = onRename
        coordinator.selectedIDs = _selectedIDs

        // Update data. Diff in a single pass — comparing via `.map(\.id) != …`
        // three times allocates six arrays per refresh on large directories.
        let itemsChanged: Bool = {
            if coordinator.items.count != items.count { return true }
            for (a, b) in zip(coordinator.items, items) {
                if a.id != b.id || a.name != b.name || a.modificationDate != b.modificationDate {
                    return true
                }
            }
            return false
        }()

        let extensionPrefChanged = coordinator.hideFileExtensions != hideFileExtensions
        coordinator.hideFileExtensions = hideFileExtensions

        coordinator.items = items

        if itemsChanged || extensionPrefChanged {
            tableView.reloadData()
            // Items changing may shift the longest filename — re-floor
            // the Name column so Auto Resize stays accurate.
            coordinator.applyNameColumnAutoResize()
        }

        // Sync selection from SwiftUI → NSTableView (only if they differ)
        let currentNSSelection = Set(tableView.selectedRowIndexes.map { items[$0].id })
        if currentNSSelection != selectedIDs {
            let indexSet = NSMutableIndexSet()
            for (index, item) in items.enumerated() {
                if selectedIDs.contains(item.id) {
                    indexSet.add(index)
                }
            }
            coordinator.suppressSelectionUpdate = true
            tableView.selectRowIndexes(indexSet as IndexSet, byExtendingSelection: false)
            coordinator.suppressSelectionUpdate = false
        }
    }
}

// MARK: - Custom NSTableView (handles Space key)

class FileTableView: NSTableView {
    var onSpaceKey: (() -> Void)?
    var onDelete: (() -> Void)?
    var onBecameFirstResponder: (() -> Void)?
    var quickLookController: QuickLookController?

    override func keyDown(with event: NSEvent) {
        if event.charactersIgnoringModifiers == " " {
            onSpaceKey?()
        } else if event.keyCode == 51 && event.modifierFlags.contains(.command) {
            // Cmd+Delete
            onDelete?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func becomeFirstResponder() -> Bool {
        let result = super.becomeFirstResponder()
        if result {
            onBecameFirstResponder?()
        }
        return result
    }

    override func mouseDown(with event: NSEvent) {
        onBecameFirstResponder?()
        super.mouseDown(with: event)
    }

    // MARK: - QLPreviewPanel support

    override func acceptsPreviewPanelControl(_ panel: QLPreviewPanel!) -> Bool {
        true
    }

    override func beginPreviewPanelControl(_ panel: QLPreviewPanel!) {
        MainActor.assumeIsolated {
            panel.dataSource = quickLookController
            panel.delegate = quickLookController
        }
    }

    override func endPreviewPanelControl(_ panel: QLPreviewPanel!) {
        MainActor.assumeIsolated {
            panel.dataSource = nil
            panel.delegate = nil
        }
    }

    // MARK: - Responder-chain Cut / Copy / Paste
    //
    // SwiftUI's menu-level `.keyboardShortcut` for ⌘C/⌘X/⌘V never wins
    // against the responder chain: macOS routes those keys via the
    // `copy:` / `cut:` / `paste:` selectors to the first responder before
    // checking menu key equivalents. By implementing those actions here
    // and posting our keyboard-command notifications, the same handlers
    // that drive the menu items run when the table is focused. Standard
    // text fields keep their own behaviour because they implement these
    // selectors themselves.

    @objc func copy(_ sender: Any?) {
        NSLog("[FileFluss] FileTableView.copy(_:) invoked")
        NotificationCenter.default.post(name: KeyboardCommand.copyFiles.notification, object: nil)
    }

    @objc func cut(_ sender: Any?) {
        NSLog("[FileFluss] FileTableView.cut(_:) invoked")
        NotificationCenter.default.post(name: KeyboardCommand.cutFiles.notification, object: nil)
    }

    @objc func paste(_ sender: Any?) {
        NSLog("[FileFluss] FileTableView.paste(_:) invoked")
        NotificationCenter.default.post(name: KeyboardCommand.pasteFiles.notification, object: nil)
    }

    /// Gate the Edit-menu items (and ⌘C/⌘X/⌘V) on whether anything is
    /// selected / clipboard has contents. Without this AppKit greys out
    /// items it doesn't know how to validate.
    override func validateUserInterfaceItem(_ item: any NSValidatedUserInterfaceItem) -> Bool {
        switch item.action {
        case #selector(copy(_:)), #selector(cut(_:)):
            return numberOfSelectedRows > 0
        case #selector(paste(_:)):
            // Allow paste when our in-app clipboard has anything, or when
            // the system pasteboard has file URLs from another app.
            return true
        default:
            return super.validateUserInterfaceItem(item)
        }
    }
}

// MARK: - Coordinator (DataSource + Delegate)

@MainActor
class FileTableCoordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSMenuDelegate {
    var items: [FileItem] = []
    var selectedIDs: Binding<Set<String>>?
    var currentDirectory: URL?
    var panelSide: PanelSide = .left
    /// When true, file rows render their name without the extension.
    /// Folders are always shown verbatim.
    var hideFileExtensions: Bool = false

    func displayedName(for item: FileItem) -> String {
        guard hideFileExtensions, !item.isDirectory else { return item.name }
        let stripped = (item.name as NSString).deletingPathExtension
        return stripped.isEmpty ? item.name : stripped
    }
    var onDoubleClick: ((FileItem) -> Void)?
    var onDrop: (([FileItem], URL) -> Void)?
    var onKeySpace: (() -> Void)?
    var onDelete: (() -> Void)?
    var onCopyToOtherPanel: (([FileItem]) -> Void)?
    var onMoveToOtherPanel: (([FileItem]) -> Void)?
    var onCalculateFolderSize: ((FileItem) -> Void)?
    var onAddToFavorites: ((FileItem) -> Void)?
    var onSortChanged: ((String, Bool) -> Void)?
    var onBecameActive: (() -> Void)?
    var onReceivePromises: ((URL) -> Void)?
    var onCreateFolder: (() -> Void)?
    var onRename: ((FileItem) -> Void)?
    weak var tableView: FileTableView?
    var suppressSelectionUpdate = false
    weak var headerMenu: NSMenu?
    nonisolated(unsafe) private var columnsObserver: NSObjectProtocol?
    nonisolated(unsafe) private var boundsObserver: NSObjectProtocol?
    nonisolated(unsafe) private var frameObserver: NSObjectProtocol?
    nonisolated(unsafe) private var activationObserver: NSObjectProtocol?
    nonisolated(unsafe) private var deminiaturizeObserver: NSObjectProtocol?
    nonisolated(unsafe) private var rowSizeObserver: NSObjectProtocol?

    // Resolved items being dragged (set when drag starts)
    private var currentDragItems: [FileItem] = []
    // Parallel set of IDs for the active drag. Cheaper than a linear search
    // through `currentDragItems` on every cursor move during validateDrop.
    private var currentDragItemIDs: Set<String> = []

    override init() {
        super.init()
        columnsObserver = NotificationCenter.default.addObserver(
            forName: .fileListColumnsChanged,
            object: nil,
            queue: .main
        ) { [weak self] note in
            // Only reapply when the local-panel prefs changed.
            if let forCloud = note.object as? Bool, forCloud { return }
            MainActor.assumeIsolated {
                guard let self, let tableView = self.tableView else { return }
                self.applyColumnVisibility(to: tableView)
            }
        }
        rowSizeObserver = NotificationCenter.default.addObserver(
            forName: .fileListRowSizeChanged,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.applyRowSize()
            }
        }
    }

    @MainActor
    private func applyRowSize() {
        guard let tableView else { return }
        tableView.rowHeight = FileListRowSizePrefs.current.rowHeight
        // reloadData re-routes through `viewFor:row:` so each cell's
        // text field picks up the freshly-sized font.
        tableView.reloadData()
    }

    deinit {
        let nc = NotificationCenter.default
        if let columnsObserver { nc.removeObserver(columnsObserver) }
        if let boundsObserver { nc.removeObserver(boundsObserver) }
        if let frameObserver { nc.removeObserver(frameObserver) }
        if let activationObserver { nc.removeObserver(activationObserver) }
        if let deminiaturizeObserver { nc.removeObserver(deminiaturizeObserver) }
        if let rowSizeObserver { nc.removeObserver(rowSizeObserver) }
    }

    /// Keep the Name column sized to fit the panel as the scroll view's clip
    /// view resizes (HSplitView drag, window resize) and when the app/window
    /// returns to the foreground after being hidden or minimized.
    func installNameColumnResizing(scrollView: NSScrollView) {
        let clipView = scrollView.contentView
        clipView.postsBoundsChangedNotifications = true
        clipView.postsFrameChangedNotifications = true

        let nc = NotificationCenter.default
        let resize: @Sendable (Notification) -> Void = { [weak self] _ in
            MainActor.assumeIsolated {
                self?.applyNameColumnAutoResize()
            }
        }
        boundsObserver = nc.addObserver(forName: NSView.boundsDidChangeNotification, object: clipView, queue: .main, using: resize)
        frameObserver = nc.addObserver(forName: NSView.frameDidChangeNotification, object: clipView, queue: .main, using: resize)
        activationObserver = nc.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main, using: resize)
        deminiaturizeObserver = nc.addObserver(forName: NSWindow.didDeminiaturizeNotification, object: nil, queue: .main, using: resize)

        // Initial fit, after the scroll view has been placed in the view tree.
        DispatchQueue.main.async { [weak self] in
            self?.applyNameColumnAutoResize()
        }
    }

    func applyColumnVisibility(to tableView: NSTableView) {
        let visible = FileListColumnPrefs.visibleColumns(forCloud: false)
        for column in tableView.tableColumns {
            switch column.identifier {
            case .dateColumn:
                column.isHidden = !visible.contains(.dateModified)
            case .dateCreatedColumn:
                column.isHidden = !visible.contains(.dateCreated)
            case .sizeColumn:
                column.isHidden = !visible.contains(.size)
            case .kindColumn:
                column.isHidden = !visible.contains(.kind)
            default:
                break
            }
        }
        applyNameColumnAutoResize()
    }

    /// Re-runs the Name-column elastic sizing with a floor derived from
    /// the current items when the Auto Resize preference is on for the
    /// local file list.
    func applyNameColumnAutoResize() {
        guard let tableView else { return }
        let floor: CGFloat? = FileListColumnPrefs.nameAutoResize(forCloud: false)
            ? FileListNameColumn.widestRequiredWidth(forNames: items.map(\.name))
            : nil
        FileListNameColumn.resizeNameColumn(in: tableView, nameColumnID: .nameColumn, floor: floor)
    }

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    // MARK: - Data Source

    func numberOfRows(in tableView: NSTableView) -> Int {
        items.count
    }

    // MARK: - Delegate (cell views)

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < items.count, let columnID = tableColumn?.identifier else { return nil }
        let item = items[row]

        switch columnID {
        case .nameColumn:
            return makeNameCell(for: item, in: tableView)
        case .dateColumn:
            return makeTextCell(
                text: Self.dateFormatter.string(from: item.modificationDate),
                identifier: .dateColumn,
                in: tableView,
                color: .secondaryLabelColor
            )
        case .dateCreatedColumn:
            return makeTextCell(
                text: item.formattedCreationDate,
                identifier: .dateCreatedColumn,
                in: tableView,
                color: .secondaryLabelColor
            )
        case .sizeColumn:
            return makeTextCell(
                text: item.formattedSize,
                identifier: .sizeColumn,
                in: tableView,
                color: .secondaryLabelColor,
                alignment: .right,
                font: FileListRowSizePrefs.current.monospacedDigitFont
            )
        case .kindColumn:
            return makeTextCell(
                text: item.kind,
                identifier: .kindColumn,
                in: tableView,
                color: .secondaryLabelColor
            )
        default:
            return nil
        }
    }

    // MARK: - Selection

    func tableViewSelectionDidChange(_ notification: Notification) {
        guard !suppressSelectionUpdate, let tableView else { return }
        let newSelection = Set(tableView.selectedRowIndexes.compactMap { index -> String? in
            guard index < items.count else { return nil }
            return items[index].id
        })
        if selectedIDs?.wrappedValue != newSelection {
            selectedIDs?.wrappedValue = newSelection
        }
    }

    // MARK: - Double-click

    @objc func handleDoubleClick(_ sender: NSTableView) {
        let row = sender.clickedRow
        guard row >= 0, row < items.count else { return }
        onDoubleClick?(items[row])
    }

    // MARK: - Sorting

    func tableView(_ tableView: NSTableView, sortDescriptorsDidChange oldDescriptors: [NSSortDescriptor]) {
        guard let descriptor = tableView.sortDescriptors.first,
              let key = descriptor.key else { return }
        onSortChanged?(key, descriptor.ascending)
    }

    // MARK: - Context Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        if menu === headerMenu {
            populateHeaderMenu(menu)
            return
        }
        menu.removeAllItems()
        guard let tableView else { return }

        let clickedRow = tableView.clickedRow

        // Right-click on empty area — Paste (if clipboard has items) + New Folder.
        if clickedRow < 0 || clickedRow >= items.count {
            let pasteItem = NSMenuItem(title: L10n.text("Paste"), action: #selector(handlePaste(_:)), keyEquivalent: "v")
            pasteItem.keyEquivalentModifierMask = [.command]
            pasteItem.target = self
            menu.addItem(pasteItem)
            menu.addItem(.separator())
            let newFolderItem = NSMenuItem(title: L10n.text("New Folder"), action: #selector(handleCreateFolder(_:)), keyEquivalent: "")
            newFolderItem.target = self
            menu.addItem(newFolderItem)
            return
        }

        // If the clicked row is not in the current selection, select only that row
        if !tableView.selectedRowIndexes.contains(clickedRow) {
            tableView.selectRowIndexes(IndexSet(integer: clickedRow), byExtendingSelection: false)
        }

        let selectedRows = tableView.selectedRowIndexes
        let contextItems = selectedRows.compactMap { index -> FileItem? in
            guard index < items.count else { return nil }
            return items[index]
        }
        guard !contextItems.isEmpty else { return }

        let otherPanelName = panelSide == .left ? L10n.text("Right") : L10n.text("Left")

        // Standard clipboard ops first so they line up with where users
        // expect them in Finder's context menu.
        let cutItem = NSMenuItem(title: L10n.text("Cut"), action: #selector(handleCut(_:)), keyEquivalent: "x")
        cutItem.keyEquivalentModifierMask = [.command]
        cutItem.target = self
        menu.addItem(cutItem)

        let copyContextItem = NSMenuItem(title: L10n.text("Copy"), action: #selector(handleCopy(_:)), keyEquivalent: "c")
        copyContextItem.keyEquivalentModifierMask = [.command]
        copyContextItem.target = self
        menu.addItem(copyContextItem)

        let pasteItem = NSMenuItem(title: L10n.text("Paste"), action: #selector(handlePaste(_:)), keyEquivalent: "v")
        pasteItem.keyEquivalentModifierMask = [.command]
        pasteItem.target = self
        menu.addItem(pasteItem)

        menu.addItem(.separator())

        let copyItem = NSMenuItem(title: L10n.format("Copy to %@ Panel", otherPanelName), action: #selector(handleCopyToOtherPanel(_:)), keyEquivalent: "")
        copyItem.target = self
        copyItem.representedObject = contextItems
        menu.addItem(copyItem)

        let moveItem = NSMenuItem(title: L10n.format("Move to %@ Panel", otherPanelName), action: #selector(handleMoveToOtherPanel(_:)), keyEquivalent: "")
        moveItem.target = self
        moveItem.representedObject = contextItems
        menu.addItem(moveItem)

        menu.addItem(.separator())

        // Rename — only for single selection
        if contextItems.count == 1 {
            let renameItem = NSMenuItem(title: L10n.text("Rename"), action: #selector(handleRename(_:)), keyEquivalent: "")
            renameItem.target = self
            renameItem.representedObject = contextItems[0]
            menu.addItem(renameItem)
        }

        let newFolderItem = NSMenuItem(title: L10n.text("New Folder"), action: #selector(handleCreateFolder(_:)), keyEquivalent: "")
        newFolderItem.target = self
        menu.addItem(newFolderItem)

        // Show folder-specific options if exactly one folder is right-clicked
        if contextItems.count == 1, let folder = contextItems.first, folder.isDirectory {
            menu.addItem(.separator())

            let favItem = NSMenuItem(title: L10n.text("Add to Favorites"), action: #selector(handleAddToFavorites(_:)), keyEquivalent: "")
            favItem.target = self
            favItem.representedObject = folder
            menu.addItem(favItem)

            let calcItem = NSMenuItem(title: L10n.text("Calculate Folder Size"), action: #selector(handleCalculateFolderSize(_:)), keyEquivalent: "")
            calcItem.target = self
            calcItem.representedObject = folder
            menu.addItem(calcItem)
        }

        menu.addItem(.separator())

        let deleteItem = NSMenuItem(title: L10n.text("Move to Trash"), action: #selector(handleDeleteFromMenu(_:)), keyEquivalent: "")
        deleteItem.target = self
        deleteItem.representedObject = contextItems
        menu.addItem(deleteItem)
    }

    @objc func handleCopyToOtherPanel(_ sender: NSMenuItem) {
        guard let contextItems = sender.representedObject as? [FileItem] else { return }
        onCopyToOtherPanel?(contextItems)
    }

    @objc func handleMoveToOtherPanel(_ sender: NSMenuItem) {
        guard let contextItems = sender.representedObject as? [FileItem] else { return }
        onMoveToOtherPanel?(contextItems)
    }

    @objc func handleAddToFavorites(_ sender: NSMenuItem) {
        guard let folder = sender.representedObject as? FileItem else { return }
        onAddToFavorites?(folder)
    }

    @objc func handleCalculateFolderSize(_ sender: NSMenuItem) {
        guard let folder = sender.representedObject as? FileItem else { return }
        onCalculateFolderSize?(folder)
    }

    @objc func handleDeleteFromMenu(_ sender: NSMenuItem) {
        onDelete?()
    }

    @objc func handleCut(_ sender: NSMenuItem) {
        NotificationCenter.default.post(name: KeyboardCommand.cutFiles.notification, object: nil)
    }

    @objc func handleCopy(_ sender: NSMenuItem) {
        NotificationCenter.default.post(name: KeyboardCommand.copyFiles.notification, object: nil)
    }

    @objc func handlePaste(_ sender: NSMenuItem) {
        NotificationCenter.default.post(name: KeyboardCommand.pasteFiles.notification, object: nil)
    }

    @objc func handleCreateFolder(_ sender: NSMenuItem) {
        onCreateFolder?()
    }

    @objc func handleRename(_ sender: NSMenuItem) {
        guard let item = sender.representedObject as? FileItem else { return }
        onRename?(item)
    }

    // MARK: - Header menu (column visibility)

    private func populateHeaderMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        let visible = FileListColumnPrefs.visibleColumns(forCloud: false)
        for column in FileListColumnPrefs.availableColumns(forCloud: false) {
            let item = NSMenuItem(title: column.title, action: #selector(toggleColumn(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = column.rawValue
            item.state = visible.contains(column) ? .on : .off
            menu.addItem(item)
        }
        // Per-column extras for the Name column. Limited to Name for
        // now because the other columns hold compact fixed-format
        // values (dates, sizes, kinds) that almost never need
        // auto-resizing in practice.
        if clickedHeaderColumnIdentifier() == .nameColumn {
            menu.addItem(.separator())
            let autoItem = NSMenuItem(title: L10n.text("Auto Resize"), action: #selector(toggleNameAutoResize(_:)), keyEquivalent: "")
            autoItem.target = self
            autoItem.state = FileListColumnPrefs.nameAutoResize(forCloud: false) ? .on : .off
            menu.addItem(autoItem)
        }
    }

    /// Which header column the user right-clicked, if any. NSMenu's
    /// delegate doesn't carry the click location, so we read it off
    /// the current event and map to a column index.
    private func clickedHeaderColumnIdentifier() -> NSUserInterfaceItemIdentifier? {
        guard let headerView = tableView?.headerView,
              let event = NSApp.currentEvent else { return nil }
        let pointInWindow = event.locationInWindow
        let pointInHeader = headerView.convert(pointInWindow, from: nil)
        let column = headerView.column(at: pointInHeader)
        guard column >= 0, let cols = tableView?.tableColumns, column < cols.count else { return nil }
        return cols[column].identifier
    }

    @objc func toggleColumn(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let id = FileListColumnID(rawValue: raw) else { return }
        FileListColumnPrefs.toggle(id, forCloud: false)
    }

    @objc func toggleNameAutoResize(_ sender: NSMenuItem) {
        let next = !FileListColumnPrefs.nameAutoResize(forCloud: false)
        FileListColumnPrefs.setNameAutoResize(next, forCloud: false)
        applyNameColumnAutoResize()
    }

    // MARK: - Drag Source

    func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> (any NSPasteboardWriting)? {
        guard row < items.count else { return nil }
        return items[row].url as NSURL
    }

    func tableView(_ tableView: NSTableView, draggingSession session: NSDraggingSession, willBeginAt screenPoint: NSPoint, forRowIndexes rowIndexes: IndexSet) {
        // Collect all dragged items
        currentDragItems = rowIndexes.compactMap { index in
            guard index < items.count else { return nil }
            return items[index]
        }
        currentDragItemIDs = Set(currentDragItems.map(\.id))
    }

    func tableView(_ tableView: NSTableView, draggingSession session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        currentDragItems = []
        currentDragItemIDs = []
    }

    // MARK: - Drop Destination

    func tableView(_ tableView: NSTableView, validateDrop info: any NSDraggingInfo, proposedRow row: Int, proposedDropOperation dropOperation: NSTableView.DropOperation) -> NSDragOperation {
        // Accept file promises from cloud panels
        let hasPromises = info.draggingPasteboard.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil)
        if hasPromises {
            // Allow drop onto a specific subfolder row; otherwise target the current directory.
            if dropOperation == .on, row >= 0, row < items.count, items[row].isDirectory {
                return .copy
            }
            tableView.setDropRow(-1, dropOperation: .on)
            return .copy
        }

        if dropOperation == .on {
            // Dropping ON a specific row — must be a directory
            guard row >= 0, row < items.count else { return [] }
            let target = items[row]
            guard target.isDirectory else { return [] }
            // Don't drop onto a dragged item itself
            if currentDragItemIDs.contains(target.id) {
                return []
            }
            return .generic
        } else {
            // Dropping between rows or on table background — target is current directory
            guard currentDirectory != nil else { return [] }
            // Retarget to the whole table (drop on background)
            tableView.setDropRow(-1, dropOperation: .on)
            return .generic
        }
    }

    func tableView(_ tableView: NSTableView, acceptDrop info: any NSDraggingInfo, row: Int, dropOperation: NSTableView.DropOperation) -> Bool {
        // Determine target directory
        let targetURL: URL
        if row >= 0, row < items.count, dropOperation == .on {
            let target = items[row]
            guard target.isDirectory else { return false }
            targetURL = target.url
        } else {
            // Dropped on table background — use current directory
            guard let dir = currentDirectory else { return false }
            targetURL = dir
        }

        // Handle file promises from cloud panels — call immediately before drag session ends
        if info.draggingPasteboard.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil) {
            onReceivePromises?(targetURL)
            return true
        }

        let droppedItems: [FileItem]
        if !currentDragItems.isEmpty {
            // Internal drag
            droppedItems = currentDragItems
        } else {
            // External drag (from Finder or other panel) — resolve URLs
            guard let urls = info.draggingPasteboard.readObjects(forClasses: [NSURL.self]) as? [URL], !urls.isEmpty else {
                return false
            }
            // First try matching against our items
            let urlPaths = Set(urls.map { $0.standardizedFileURL.path() })
            let matched = items.filter { urlPaths.contains($0.url.standardizedFileURL.path()) }
            if !matched.isEmpty {
                droppedItems = matched
            } else {
                // Build FileItems from URLs (cross-panel or external drag)
                droppedItems = urls.compactMap { url -> FileItem? in
                    guard FileManager.default.fileExists(atPath: url.path) else { return nil }
                    return FileItem(url: url)
                }
                guard !droppedItems.isEmpty else { return false }
            }
        }

        // Standardize the target URL once — recomputing it for every dropped
        // item (and inside `allInSameDir`) is wasteful on large drops.
        let standardizedTarget = targetURL.standardizedFileURL

        // Don't drop a folder into itself
        guard !droppedItems.contains(where: { $0.url.standardizedFileURL == standardizedTarget }) else { return false }

        // Don't drop into the same directory the items are already in
        let allInSameDir = droppedItems.allSatisfy {
            $0.url.deletingLastPathComponent().standardizedFileURL == standardizedTarget
        }
        guard !allInSameDir else { return false }

        onDrop?(droppedItems, targetURL)
        return true
    }

    // MARK: - Cell Factories

    private func makeNameCell(for item: FileItem, in tableView: NSTableView) -> NSTableCellView {
        let id = NSUserInterfaceItemIdentifier("NameCell")
        let cell: NSTableCellView

        if let reused = tableView.makeView(withIdentifier: id, owner: nil) as? NSTableCellView {
            cell = reused
        } else {
            cell = NSTableCellView()
            cell.identifier = id

            let imageView = NSImageView()
            imageView.translatesAutoresizingMaskIntoConstraints = false
            imageView.setContentHuggingPriority(.required, for: .horizontal)

            let textField = NSTextField(labelWithString: "")
            textField.translatesAutoresizingMaskIntoConstraints = false
            textField.lineBreakMode = .byTruncatingMiddle
            textField.cell?.truncatesLastVisibleLine = true

            cell.addSubview(imageView)
            cell.addSubview(textField)
            cell.imageView = imageView
            cell.textField = textField

            NSLayoutConstraint.activate([
                imageView.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 2),
                imageView.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
                imageView.widthAnchor.constraint(equalToConstant: 18),
                imageView.heightAnchor.constraint(equalToConstant: 18),
                textField.leadingAnchor.constraint(equalTo: imageView.trailingAnchor, constant: 6),
                textField.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -2),
                textField.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            ])
        }

        cell.textField?.stringValue = displayedName(for: item)
        cell.textField?.textColor = .labelColor
        cell.textField?.font = FileListRowSizePrefs.current.systemFont

        let baseIcon: NSImage = item.isDirectory
            ? FileTypeIcon.folderIcon()
            : FileTypeIcon.icon(for: item.contentType)
        cell.imageView?.contentTintColor = nil

        // Tag the imageView so an in-flight thumbnail request from a prior
        // (now-reused) row doesn't paint the wrong image when it finishes.
        let itemID = item.id
        cell.imageView?.identifier = NSUserInterfaceItemIdentifier(itemID)

        if !item.isDirectory, ThumbnailCache.shouldUseThumbnail(for: item.contentType) {
            if let thumb = ThumbnailCache.shared.cached(for: item.url, mtime: item.modificationDate, size: item.size) {
                cell.imageView?.image = thumb
            } else {
                cell.imageView?.image = baseIcon
                let url = item.url
                let mtime = item.modificationDate
                let size = item.size
                let imageView = cell.imageView
                Task { @MainActor in
                    let thumb = await ThumbnailCache.shared.thumbnail(for: url, mtime: mtime, size: size)
                    guard let thumb,
                          let imageView,
                          imageView.identifier?.rawValue == itemID else { return }
                    imageView.image = thumb
                }
            }
        } else {
            cell.imageView?.image = baseIcon
        }

        return cell
    }

    private func makeTextCell(
        text: String,
        identifier: NSUserInterfaceItemIdentifier,
        in tableView: NSTableView,
        color: NSColor = .labelColor,
        alignment: NSTextAlignment = .left,
        font: NSFont = FileListRowSizePrefs.current.systemFont
    ) -> NSTableCellView {
        let id = NSUserInterfaceItemIdentifier(identifier.rawValue + "Cell")
        let cell: NSTableCellView

        if let reused = tableView.makeView(withIdentifier: id, owner: nil) as? NSTableCellView {
            cell = reused
        } else {
            cell = NSTableCellView()
            cell.identifier = id

            let textField = NSTextField(labelWithString: "")
            textField.translatesAutoresizingMaskIntoConstraints = false
            textField.lineBreakMode = .byTruncatingTail

            cell.addSubview(textField)
            cell.textField = textField

            NSLayoutConstraint.activate([
                textField.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 2),
                textField.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -2),
                textField.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            ])
        }

        cell.textField?.stringValue = text
        cell.textField?.textColor = color
        cell.textField?.alignment = alignment
        cell.textField?.font = font

        return cell
    }
}

// MARK: - Column Identifiers

private extension NSUserInterfaceItemIdentifier {
    static let nameColumn = NSUserInterfaceItemIdentifier("NameColumn")
    static let dateColumn = NSUserInterfaceItemIdentifier("DateColumn")
    static let dateCreatedColumn = NSUserInterfaceItemIdentifier("DateCreatedColumn")
    static let sizeColumn = NSUserInterfaceItemIdentifier("SizeColumn")
    static let kindColumn = NSUserInterfaceItemIdentifier("KindColumn")
}
