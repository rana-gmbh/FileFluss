import SwiftUI
import AppKit
import UniformTypeIdentifiers

// MARK: - NSViewRepresentable Bridge

struct NativeFileList: NSViewRepresentable {
    let items: [FileItem]
    let currentDirectory: URL
    let panelSide: PanelSide
    @Binding var selectedIDs: Set<String>
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
        tableView.rowHeight = 24
        tableView.intercellSpacing = NSSize(width: 10, height: 4)
        tableView.headerView = NSTableHeaderView()
        tableView.gridStyleMask = []

        // Name column
        let nameCol = NSTableColumn(identifier: .nameColumn)
        nameCol.title = "Name"
        nameCol.minWidth = 200
        nameCol.sortDescriptorPrototype = NSSortDescriptor(key: "name", ascending: true, selector: #selector(NSString.localizedStandardCompare(_:)))
        tableView.addTableColumn(nameCol)

        // Date column
        let dateCol = NSTableColumn(identifier: .dateColumn)
        dateCol.title = "Date Modified"
        dateCol.width = 160
        dateCol.minWidth = 100
        dateCol.sortDescriptorPrototype = NSSortDescriptor(key: "date", ascending: true)
        tableView.addTableColumn(dateCol)

        // Size column
        let sizeCol = NSTableColumn(identifier: .sizeColumn)
        sizeCol.title = "Size"
        sizeCol.width = 80
        sizeCol.minWidth = 60
        sizeCol.sortDescriptorPrototype = NSSortDescriptor(key: "size", ascending: true)
        tableView.addTableColumn(sizeCol)

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

        // Update data
        let itemsChanged = coordinator.items.map(\.id) != items.map(\.id)
            || coordinator.items.map(\.name) != items.map(\.name)
            || coordinator.items.map(\.modificationDate) != items.map(\.modificationDate)

        coordinator.items = items

        if itemsChanged {
            tableView.reloadData()
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
}

// MARK: - Coordinator (DataSource + Delegate)

@MainActor
class FileTableCoordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSMenuDelegate {
    var items: [FileItem] = []
    var selectedIDs: Binding<Set<String>>?
    var currentDirectory: URL?
    var panelSide: PanelSide = .left
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

    // Resolved items being dragged (set when drag starts)
    private var currentDragItems: [FileItem] = []

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
        case .sizeColumn:
            return makeTextCell(
                text: item.formattedSize,
                identifier: .sizeColumn,
                in: tableView,
                color: .secondaryLabelColor,
                alignment: .right,
                font: .monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
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
        menu.removeAllItems()
        guard let tableView else { return }

        let clickedRow = tableView.clickedRow

        // Right-click on empty area — show "New Folder" only
        if clickedRow < 0 || clickedRow >= items.count {
            let newFolderItem = NSMenuItem(title: "New Folder", action: #selector(handleCreateFolder(_:)), keyEquivalent: "")
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

        let otherPanelName = panelSide == .left ? "Right" : "Left"

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

        let newFolderItem = NSMenuItem(title: "New Folder", action: #selector(handleCreateFolder(_:)), keyEquivalent: "")
        newFolderItem.target = self
        menu.addItem(newFolderItem)

        // Show folder-specific options if exactly one folder is right-clicked
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

        let deleteItem = NSMenuItem(title: "Move to Trash", action: #selector(handleDeleteFromMenu(_:)), keyEquivalent: "")
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

    @objc func handleCreateFolder(_ sender: NSMenuItem) {
        onCreateFolder?()
    }

    @objc func handleRename(_ sender: NSMenuItem) {
        guard let item = sender.representedObject as? FileItem else { return }
        onRename?(item)
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
    }

    func tableView(_ tableView: NSTableView, draggingSession session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        currentDragItems = []
    }

    // MARK: - Drop Destination

    func tableView(_ tableView: NSTableView, validateDrop info: any NSDraggingInfo, proposedRow row: Int, proposedDropOperation dropOperation: NSTableView.DropOperation) -> NSDragOperation {
        // Accept file promises from cloud panels
        let hasPromises = info.draggingPasteboard.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil)
        if hasPromises {
            tableView.setDropRow(-1, dropOperation: .on)
            return .copy
        }

        if dropOperation == .on {
            // Dropping ON a specific row — must be a directory
            guard row >= 0, row < items.count else { return [] }
            let target = items[row]
            guard target.isDirectory else { return [] }
            // Don't drop onto a dragged item itself
            if currentDragItems.contains(where: { $0.id == target.id }) {
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

        // Don't drop a folder into itself
        guard !droppedItems.contains(where: { $0.url.standardizedFileURL == targetURL.standardizedFileURL }) else { return false }

        // Don't drop into the same directory the items are already in
        let allInSameDir = droppedItems.allSatisfy {
            $0.url.deletingLastPathComponent().standardizedFileURL == targetURL.standardizedFileURL
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

        cell.textField?.stringValue = item.name
        cell.textField?.textColor = .labelColor

        let icon = NSImage(systemSymbolName: item.icon, accessibilityDescription: nil)
        cell.imageView?.image = icon
        cell.imageView?.contentTintColor = item.isDirectory ? .controlAccentColor : .secondaryLabelColor

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

// MARK: - Column Identifiers

private extension NSUserInterfaceItemIdentifier {
    static let nameColumn = NSUserInterfaceItemIdentifier("NameColumn")
    static let dateColumn = NSUserInterfaceItemIdentifier("DateColumn")
    static let sizeColumn = NSUserInterfaceItemIdentifier("SizeColumn")
}
