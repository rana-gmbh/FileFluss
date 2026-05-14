import SwiftUI
import AppKit
import UniformTypeIdentifiers
import QuickLookUI

// MARK: - NSViewRepresentable Bridge

struct NativeCloudFileList: NSViewRepresentable {
    let items: [CloudFileItem]
    let panelSide: PanelSide
    var hideFileExtensions: Bool = false
    @Binding var selectedIDs: Set<String>
    var quickLookController: QuickLookController?
    var onDoubleClick: (CloudFileItem) -> Void
    var onDrop: (([URL], CloudFileItem?) -> Void)?
    var onKeySpace: () -> Void
    var onDelete: (() -> Void)?
    var onCopyToOtherPanel: (([CloudFileItem]) -> Void)?
    var onMoveToOtherPanel: (([CloudFileItem]) -> Void)?
    var onCalculateFolderSize: ((CloudFileItem) -> Void)?
    var onAddToFavorites: ((CloudFileItem) -> Void)?
    var onSortChanged: ((String, Bool) -> Void)?
    var onBecameActive: (() -> Void)?
    var onDownloadToTemp: ((CloudFileItem, @escaping (URL?) -> Void) -> Void)?
    var onDragSessionStarted: (([CloudFileItem]) -> Void)?
    var onDragSessionEnded: (() -> Void)?
    var onReceiveCloudDrop: ((CloudFileItem?) -> Void)?
    var onInternalCloudDrop: (([CloudFileItem], CloudFileItem) -> Void)?
    var onCreateFolder: (() -> Void)?
    var onRename: ((CloudFileItem) -> Void)?
    var canCreateFolder: Bool = true

    func makeCoordinator() -> CloudTableCoordinator {
        CloudTableCoordinator()
    }

    func makeNSView(context: Context) -> NSScrollView {
        let coordinator = context.coordinator

        let tableView = CloudTableView()
        tableView.style = .inset
        tableView.usesAlternatingRowBackgroundColors = true
        tableView.allowsMultipleSelection = true
        tableView.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        tableView.rowHeight = 24
        tableView.intercellSpacing = NSSize(width: 10, height: 4)
        tableView.headerView = NSTableHeaderView()
        tableView.gridStyleMask = []

        let nameCol = NSTableColumn(identifier: .cloudNameColumn)
        nameCol.title = "Name"
        nameCol.minWidth = FileListNameColumn.minWidth
        nameCol.sortDescriptorPrototype = NSSortDescriptor(key: "name", ascending: true, selector: #selector(NSString.localizedStandardCompare(_:)))
        tableView.addTableColumn(nameCol)

        let dateCol = NSTableColumn(identifier: .cloudDateColumn)
        dateCol.title = "Date Modified"
        dateCol.width = 160
        dateCol.minWidth = 100
        dateCol.sortDescriptorPrototype = NSSortDescriptor(key: "date", ascending: true)
        tableView.addTableColumn(dateCol)

        let sizeCol = NSTableColumn(identifier: .cloudSizeColumn)
        sizeCol.title = "Size"
        sizeCol.width = 80
        sizeCol.minWidth = 60
        sizeCol.sortDescriptorPrototype = NSSortDescriptor(key: "size", ascending: true)
        tableView.addTableColumn(sizeCol)

        let kindCol = NSTableColumn(identifier: .cloudKindColumn)
        kindCol.title = "Kind"
        kindCol.width = 120
        kindCol.minWidth = 80
        kindCol.sortDescriptorPrototype = NSSortDescriptor(key: "kind", ascending: true, selector: #selector(NSString.localizedStandardCompare(_:)))
        tableView.addTableColumn(kindCol)

        coordinator.applyColumnVisibility(to: tableView)

        let headerMenu = NSMenu()
        headerMenu.delegate = coordinator
        coordinator.headerMenu = headerMenu
        tableView.headerView?.menu = headerMenu

        tableView.sizeLastColumnToFit()

        tableView.dataSource = coordinator
        tableView.delegate = coordinator

        tableView.doubleAction = #selector(coordinator.handleDoubleClick(_:))
        tableView.target = coordinator

        tableView.registerForDraggedTypes([.fileURL, .filePromise])
        tableView.setDraggingSourceOperationMask(.every, forLocal: true)
        tableView.setDraggingSourceOperationMask(.every, forLocal: false)

        tableView.onSpaceKey = { [weak coordinator] in
            coordinator?.onKeySpace?()
        }
        tableView.onDelete = { [weak coordinator] in
            coordinator?.onDelete?()
        }
        tableView.onBecameFirstResponder = { [weak coordinator] in
            coordinator?.onBecameActive?()
        }

        // Quick Look controller
        if let qlController = quickLookController {
            tableView.quickLookController = qlController
            qlController.sourceTableView = tableView
        }

        let menu = NSMenu()
        menu.delegate = coordinator
        tableView.menu = menu

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
        coordinator.onDownloadToTemp = onDownloadToTemp
        coordinator.onDragSessionStarted = onDragSessionStarted
        coordinator.onDragSessionEnded = onDragSessionEnded
        coordinator.onReceiveCloudDrop = onReceiveCloudDrop
        coordinator.onInternalCloudDrop = onInternalCloudDrop
        coordinator.onCreateFolder = onCreateFolder
        coordinator.onRename = onRename
        coordinator.canCreateFolder = canCreateFolder
        coordinator.selectedIDs = _selectedIDs

        // Diff in a single pass — comparing via `.map(\.id) != …` three times
        // allocates six arrays per refresh on large directories.
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
        }

        let currentNSSelection = Set(tableView.selectedRowIndexes.compactMap { idx -> String? in
            guard idx < items.count else { return nil }
            return items[idx].id
        })
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

// MARK: - Custom NSTableView

class CloudTableView: NSTableView {
    var onSpaceKey: (() -> Void)?
    var onDelete: (() -> Void)?
    var onBecameFirstResponder: (() -> Void)?
    var quickLookController: QuickLookController?

    override func keyDown(with event: NSEvent) {
        if event.charactersIgnoringModifiers == " " {
            onSpaceKey?()
        } else if event.keyCode == 51 && event.modifierFlags.contains(.command) {
            onDelete?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func becomeFirstResponder() -> Bool {
        let result = super.becomeFirstResponder()
        if result { onBecameFirstResponder?() }
        return result
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

    override func mouseDown(with event: NSEvent) {
        onBecameFirstResponder?()
        super.mouseDown(with: event)
    }

    // Responder-chain Cut/Copy/Paste — see the long-form comment on
    // FileTableView for why this is the only reliable way to bind
    // ⌘C/⌘X/⌘V on macOS.
    @objc func copy(_ sender: Any?) {
        NotificationCenter.default.post(name: KeyboardCommand.copyFiles.notification, object: nil)
    }

    @objc func cut(_ sender: Any?) {
        NotificationCenter.default.post(name: KeyboardCommand.cutFiles.notification, object: nil)
    }

    @objc func paste(_ sender: Any?) {
        NotificationCenter.default.post(name: KeyboardCommand.pasteFiles.notification, object: nil)
    }

    override func validateUserInterfaceItem(_ item: any NSValidatedUserInterfaceItem) -> Bool {
        switch item.action {
        case #selector(copy(_:)), #selector(cut(_:)):
            return numberOfSelectedRows > 0
        case #selector(paste(_:)):
            return true
        default:
            return super.validateUserInterfaceItem(item)
        }
    }
}

// MARK: - Coordinator

@MainActor
class CloudTableCoordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSMenuDelegate {
    var items: [CloudFileItem] = []
    var selectedIDs: Binding<Set<String>>?
    var panelSide: PanelSide = .left
    /// When true, file rows render their name without the extension.
    /// Folders are always shown verbatim.
    var hideFileExtensions: Bool = false

    func displayedName(for item: CloudFileItem) -> String {
        guard hideFileExtensions, !item.isDirectory else { return item.name }
        let stripped = (item.name as NSString).deletingPathExtension
        return stripped.isEmpty ? item.name : stripped
    }
    var onDoubleClick: ((CloudFileItem) -> Void)?
    var onDrop: (([URL], CloudFileItem?) -> Void)?
    var onKeySpace: (() -> Void)?
    var onDelete: (() -> Void)?
    var onCopyToOtherPanel: (([CloudFileItem]) -> Void)?
    var onMoveToOtherPanel: (([CloudFileItem]) -> Void)?
    var onCalculateFolderSize: ((CloudFileItem) -> Void)?
    var onAddToFavorites: ((CloudFileItem) -> Void)?
    var onSortChanged: ((String, Bool) -> Void)?
    var onBecameActive: (() -> Void)?
    var onDownloadToTemp: ((CloudFileItem, @escaping (URL?) -> Void) -> Void)?
    var onDragSessionStarted: (([CloudFileItem]) -> Void)?
    var onDragSessionEnded: (() -> Void)?
    var onReceiveCloudDrop: ((CloudFileItem?) -> Void)?
    var onInternalCloudDrop: (([CloudFileItem], CloudFileItem) -> Void)?
    var onCreateFolder: (() -> Void)?
    var onRename: ((CloudFileItem) -> Void)?
    var canCreateFolder: Bool = true
    weak var tableView: CloudTableView?
    var suppressSelectionUpdate = false
    weak var headerMenu: NSMenu?
    nonisolated(unsafe) private var columnsObserver: NSObjectProtocol?
    nonisolated(unsafe) private var boundsObserver: NSObjectProtocol?
    nonisolated(unsafe) private var frameObserver: NSObjectProtocol?
    nonisolated(unsafe) private var activationObserver: NSObjectProtocol?
    nonisolated(unsafe) private var deminiaturizeObserver: NSObjectProtocol?
    let filePromiseDelegate = CloudFilePromiseDelegate()

    private var currentDragItems: [CloudFileItem] = []

    override init() {
        super.init()
        columnsObserver = NotificationCenter.default.addObserver(
            forName: .fileListColumnsChanged,
            object: nil,
            queue: .main
        ) { [weak self] note in
            // Only reapply when the cloud-panel prefs changed.
            if let forCloud = note.object as? Bool, !forCloud { return }
            MainActor.assumeIsolated {
                guard let self, let tableView = self.tableView else { return }
                self.applyColumnVisibility(to: tableView)
            }
        }
    }

    deinit {
        let nc = NotificationCenter.default
        if let columnsObserver { nc.removeObserver(columnsObserver) }
        if let boundsObserver { nc.removeObserver(boundsObserver) }
        if let frameObserver { nc.removeObserver(frameObserver) }
        if let activationObserver { nc.removeObserver(activationObserver) }
        if let deminiaturizeObserver { nc.removeObserver(deminiaturizeObserver) }
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
                guard let self, let tv = self.tableView else { return }
                FileListNameColumn.resizeNameColumn(in: tv, nameColumnID: .cloudNameColumn)
            }
        }
        boundsObserver = nc.addObserver(forName: NSView.boundsDidChangeNotification, object: clipView, queue: .main, using: resize)
        frameObserver = nc.addObserver(forName: NSView.frameDidChangeNotification, object: clipView, queue: .main, using: resize)
        activationObserver = nc.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main, using: resize)
        deminiaturizeObserver = nc.addObserver(forName: NSWindow.didDeminiaturizeNotification, object: nil, queue: .main, using: resize)

        DispatchQueue.main.async { [weak self] in
            guard let self, let tv = self.tableView else { return }
            FileListNameColumn.resizeNameColumn(in: tv, nameColumnID: .cloudNameColumn)
        }
    }

    func applyColumnVisibility(to tableView: NSTableView) {
        let visible = FileListColumnPrefs.visibleColumns(forCloud: true)
        for column in tableView.tableColumns {
            switch column.identifier {
            case .cloudDateColumn:
                column.isHidden = !visible.contains(.dateModified)
            case .cloudSizeColumn:
                column.isHidden = !visible.contains(.size)
            case .cloudKindColumn:
                column.isHidden = !visible.contains(.kind)
            default:
                break
            }
        }
        FileListNameColumn.resizeNameColumn(in: tableView, nameColumnID: .cloudNameColumn)
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

    // MARK: - Delegate

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < items.count, let columnID = tableColumn?.identifier else { return nil }
        let item = items[row]

        switch columnID {
        case .cloudNameColumn:
            return makeNameCell(for: item, in: tableView)
        case .cloudDateColumn:
            return makeTextCell(
                text: item.formattedDate,
                identifier: .cloudDateColumn,
                in: tableView,
                color: .secondaryLabelColor
            )
        case .cloudSizeColumn:
            return makeTextCell(
                text: item.formattedSize,
                identifier: .cloudSizeColumn,
                in: tableView,
                color: .secondaryLabelColor,
                alignment: .right,
                font: .monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
            )
        case .cloudKindColumn:
            return makeTextCell(
                text: item.kind,
                identifier: .cloudKindColumn,
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

        // Right-click on empty area — offer Paste + New Folder.
        if clickedRow < 0 || clickedRow >= items.count {
            let pasteItem = NSMenuItem(title: "Paste", action: #selector(handlePaste(_:)), keyEquivalent: "v")
            pasteItem.keyEquivalentModifierMask = [.command]
            pasteItem.target = self
            menu.addItem(pasteItem)
            if canCreateFolder {
                menu.addItem(.separator())
                let newFolderItem = NSMenuItem(title: "New Folder", action: #selector(handleCreateFolder(_:)), keyEquivalent: "")
                newFolderItem.target = self
                menu.addItem(newFolderItem)
            }
            return
        }

        if !tableView.selectedRowIndexes.contains(clickedRow) {
            tableView.selectRowIndexes(IndexSet(integer: clickedRow), byExtendingSelection: false)
        }

        let selectedRows = tableView.selectedRowIndexes
        let contextItems = selectedRows.compactMap { index -> CloudFileItem? in
            guard index < items.count else { return nil }
            return items[index]
        }
        guard !contextItems.isEmpty else { return }

        let otherPanelName = panelSide == .left ? "Right" : "Left"

        let cutCtx = NSMenuItem(title: "Cut", action: #selector(handleCut(_:)), keyEquivalent: "x")
        cutCtx.keyEquivalentModifierMask = [.command]
        cutCtx.target = self
        menu.addItem(cutCtx)

        let copyCtx = NSMenuItem(title: "Copy", action: #selector(handleCopy(_:)), keyEquivalent: "c")
        copyCtx.keyEquivalentModifierMask = [.command]
        copyCtx.target = self
        menu.addItem(copyCtx)

        let pasteCtx = NSMenuItem(title: "Paste", action: #selector(handlePaste(_:)), keyEquivalent: "v")
        pasteCtx.keyEquivalentModifierMask = [.command]
        pasteCtx.target = self
        menu.addItem(pasteCtx)

        menu.addItem(.separator())

        let copyItem = NSMenuItem(title: "Copy to \(otherPanelName) Panel", action: #selector(handleCopyToOtherPanel(_:)), keyEquivalent: "")
        copyItem.target = self
        copyItem.representedObject = contextItems
        menu.addItem(copyItem)

        let moveItem = NSMenuItem(title: "Move to \(otherPanelName) Panel", action: #selector(handleMoveToOtherPanel(_:)), keyEquivalent: "")
        moveItem.target = self
        moveItem.representedObject = contextItems
        menu.addItem(moveItem)

        menu.addItem(.separator())

        // Rename — only for single selection
        if contextItems.count == 1 {
            let renameItem = NSMenuItem(title: "Rename", action: #selector(handleRename(_:)), keyEquivalent: "")
            renameItem.target = self
            renameItem.representedObject = contextItems[0]
            menu.addItem(renameItem)
        }

        if canCreateFolder {
            let newFolderItem = NSMenuItem(title: "New Folder", action: #selector(handleCreateFolder(_:)), keyEquivalent: "")
            newFolderItem.target = self
            menu.addItem(newFolderItem)
        }

        if contextItems.count == 1, let folder = contextItems.first, folder.isDirectory {
            menu.addItem(.separator())

            let favItem = NSMenuItem(title: "Add to Favorites", action: #selector(handleAddToFavorites(_:)), keyEquivalent: "")
            favItem.target = self
            favItem.representedObject = folder
            menu.addItem(favItem)

            let calcItem = NSMenuItem(title: "Calculate Folder Size", action: #selector(handleCalculateFolderSize(_:)), keyEquivalent: "")
            calcItem.target = self
            calcItem.representedObject = folder
            menu.addItem(calcItem)
        }

        menu.addItem(.separator())

        let deleteItem = NSMenuItem(title: "Delete", action: #selector(handleDeleteFromMenu(_:)), keyEquivalent: "")
        deleteItem.target = self
        deleteItem.representedObject = contextItems
        menu.addItem(deleteItem)
    }

    @objc func handleCopyToOtherPanel(_ sender: NSMenuItem) {
        guard let contextItems = sender.representedObject as? [CloudFileItem] else { return }
        onCopyToOtherPanel?(contextItems)
    }

    @objc func handleMoveToOtherPanel(_ sender: NSMenuItem) {
        guard let contextItems = sender.representedObject as? [CloudFileItem] else { return }
        onMoveToOtherPanel?(contextItems)
    }

    @objc func handleAddToFavorites(_ sender: NSMenuItem) {
        guard let folder = sender.representedObject as? CloudFileItem else { return }
        onAddToFavorites?(folder)
    }

    @objc func handleCalculateFolderSize(_ sender: NSMenuItem) {
        guard let folder = sender.representedObject as? CloudFileItem else { return }
        onCalculateFolderSize?(folder)
    }

    @objc func handleCreateFolder(_ sender: NSMenuItem) {
        onCreateFolder?()
    }

    @objc func handleRename(_ sender: NSMenuItem) {
        guard let item = sender.representedObject as? CloudFileItem else { return }
        onRename?(item)
    }

    // MARK: - Header menu (column visibility)

    private func populateHeaderMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        let visible = FileListColumnPrefs.visibleColumns(forCloud: true)
        for column in FileListColumnPrefs.availableColumns(forCloud: true) {
            let item = NSMenuItem(title: column.title, action: #selector(toggleColumn(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = column.rawValue
            item.state = visible.contains(column) ? .on : .off
            menu.addItem(item)
        }
    }

    @objc func toggleColumn(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let id = FileListColumnID(rawValue: raw) else { return }
        FileListColumnPrefs.toggle(id, forCloud: true)
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

    // MARK: - Drag Source

    func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> (any NSPasteboardWriting)? {
        guard row < items.count else { return nil }
        let item = items[row]

        filePromiseDelegate.downloadHandler = onDownloadToTemp
        let fileType = item.isDirectory ? UTType.folder.identifier : UTType.data.identifier
        let provider = NSFilePromiseProvider(fileType: fileType, delegate: filePromiseDelegate)
        provider.userInfo = item
        return provider
    }

    func tableView(_ tableView: NSTableView, draggingSession session: NSDraggingSession, willBeginAt screenPoint: NSPoint, forRowIndexes rowIndexes: IndexSet) {
        currentDragItems = rowIndexes.compactMap { index in
            guard index < items.count else { return nil }
            return items[index]
        }
        onDragSessionStarted?(currentDragItems)
    }

    func tableView(_ tableView: NSTableView, draggingSession session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        currentDragItems = []
        onDragSessionEnded?()
    }

    // MARK: - Drop Destination (accept local files for upload)

    func tableView(_ tableView: NSTableView, validateDrop info: any NSDraggingInfo, proposedRow row: Int, proposedDropOperation dropOperation: NSTableView.DropOperation) -> NSDragOperation {
        // Internal drag from the same cloud panel: allow drop onto a subfolder row
        if !currentDragItems.isEmpty, dropOperation == .on, row >= 0, row < items.count {
            let target = items[row]
            if target.isDirectory, !currentDragItems.contains(where: { $0.id == target.id }) {
                return .generic
            }
        }

        let hasPromises = info.draggingPasteboard.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil)
        let hasURLs = info.draggingPasteboard.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true])

        if hasPromises || hasURLs {
            // Allow dropping onto a specific subfolder row
            if dropOperation == .on, row >= 0, row < items.count, items[row].isDirectory {
                return hasPromises ? .copy : .generic
            }
            // Fall back to dropping on the background (current directory)
            tableView.setDropRow(-1, dropOperation: .on)
            return hasPromises ? .copy : .generic
        }
        return []
    }

    func tableView(_ tableView: NSTableView, acceptDrop info: any NSDraggingInfo, row: Int, dropOperation: NSTableView.DropOperation) -> Bool {
        // Resolve an optional subfolder target from the drop row
        var targetFolder: CloudFileItem?
        if dropOperation == .on, row >= 0, row < items.count, items[row].isDirectory {
            targetFolder = items[row]
        }

        // Internal drag: drop onto a subfolder in the same panel
        if !currentDragItems.isEmpty, let target = targetFolder,
           !currentDragItems.contains(where: { $0.id == target.id }) {
            onInternalCloudDrop?(currentDragItems, target)
            return true
        }

        // Handle file promises from other cloud panels — call immediately before drag session ends
        if info.draggingPasteboard.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil) {
            onReceiveCloudDrop?(targetFolder)
            return true
        }

        guard let urls = info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty else {
            return false
        }
        onDrop?(urls, targetFolder)
        return true
    }

    // MARK: - Cell Factories

    private static let cloudBadgeTag = 9911

    private func makeNameCell(for item: CloudFileItem, in tableView: NSTableView) -> NSTableCellView {
        let id = NSUserInterfaceItemIdentifier("CloudNameCell")
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

            // Small overlay badge for iCloud evicted / downloading items.
            // Pinned to the bottom-right of the file icon so it overlaps
            // the corner without obscuring file-type recognition.
            let badge = NSImageView()
            badge.tag = Self.cloudBadgeTag
            badge.translatesAutoresizingMaskIntoConstraints = false
            badge.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 10, weight: .semibold)
            badge.isHidden = true

            cell.addSubview(imageView)
            cell.addSubview(textField)
            cell.addSubview(badge)
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
                badge.trailingAnchor.constraint(equalTo: imageView.trailingAnchor, constant: 2),
                badge.bottomAnchor.constraint(equalTo: imageView.bottomAnchor, constant: 2),
                badge.widthAnchor.constraint(equalToConstant: 11),
                badge.heightAnchor.constraint(equalToConstant: 11),
            ])
        }

        cell.textField?.stringValue = displayedName(for: item)
        cell.textField?.textColor = .labelColor

        if item.isDirectory {
            cell.imageView?.image = FileTypeIcon.folderIcon()
        } else {
            cell.imageView?.image = FreeFileIcon.icon(forFilename: item.name)
                ?? FileTypeIcon.icon(forFilename: item.name)
        }
        cell.imageView?.contentTintColor = nil

        if let badge = cell.viewWithTag(Self.cloudBadgeTag) as? NSImageView {
            switch item.downloadStatus {
            case .local:
                badge.isHidden = true
                badge.image = nil
            case .evicted:
                badge.isHidden = false
                badge.image = NSImage(systemSymbolName: "icloud.and.arrow.down", accessibilityDescription: "Not downloaded")
                badge.contentTintColor = .secondaryLabelColor
            case .downloading:
                badge.isHidden = false
                badge.image = NSImage(systemSymbolName: "arrow.down.circle.fill", accessibilityDescription: "Downloading")
                badge.contentTintColor = .controlAccentColor
            }
        }

        return cell
    }

    private func makeTextCell(
        text: String,
        identifier: NSUserInterfaceItemIdentifier,
        in tableView: NSTableView,
        color: NSColor = .labelColor,
        alignment: NSTextAlignment = .left,
        font: NSFont = .systemFont(ofSize: NSFont.systemFontSize)
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

// MARK: - File Promise Delegate Helper

class CloudFilePromiseDelegate: NSObject, NSFilePromiseProviderDelegate, @unchecked Sendable {
    nonisolated(unsafe) var downloadHandler: ((CloudFileItem, @escaping (URL?) -> Void) -> Void)?

    func filePromiseProvider(_ filePromiseProvider: NSFilePromiseProvider, fileNameForType fileType: String) -> String {
        guard let item = filePromiseProvider.userInfo as? CloudFileItem else { return "unknown" }
        return item.name
    }

    func filePromiseProvider(_ filePromiseProvider: NSFilePromiseProvider, writePromiseTo url: URL, completionHandler handler: @escaping ((any Error)?) -> Void) {
        // No-op: actual download is triggered by the copy/move dialog on the receiving side
        handler(nil)
    }

    nonisolated func operationQueue(for filePromiseProvider: NSFilePromiseProvider) -> OperationQueue {
        .main
    }
}

// MARK: - Column Identifiers

private extension NSUserInterfaceItemIdentifier {
    static let cloudNameColumn = NSUserInterfaceItemIdentifier("CloudNameColumn")
    static let cloudDateColumn = NSUserInterfaceItemIdentifier("CloudDateColumn")
    static let cloudSizeColumn = NSUserInterfaceItemIdentifier("CloudSizeColumn")
    static let cloudKindColumn = NSUserInterfaceItemIdentifier("CloudKindColumn")
}
