import SwiftUI
import AppKit
import UniformTypeIdentifiers
import QuickLookUI

// MARK: - NSViewRepresentable Bridge

struct NativeCloudFileList: NSViewRepresentable {
    let items: [CloudFileItem]
    let panelSide: PanelSide
    @Binding var selectedIDs: Set<String>
    var quickLookController: QuickLookController?
    var onDoubleClick: (CloudFileItem) -> Void
    var onDrop: (([URL]) -> Void)?
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
    var onReceiveCloudDrop: (() -> Void)?
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
        nameCol.minWidth = 200
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
        coordinator.onCreateFolder = onCreateFolder
        coordinator.onRename = onRename
        coordinator.canCreateFolder = canCreateFolder
        coordinator.selectedIDs = _selectedIDs

        let itemsChanged = coordinator.items.map(\.id) != items.map(\.id)
            || coordinator.items.map(\.name) != items.map(\.name)
            || coordinator.items.map(\.modificationDate) != items.map(\.modificationDate)

        coordinator.items = items

        if itemsChanged {
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
}

// MARK: - Coordinator

@MainActor
class CloudTableCoordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSMenuDelegate {
    var items: [CloudFileItem] = []
    var selectedIDs: Binding<Set<String>>?
    var panelSide: PanelSide = .left
    var onDoubleClick: ((CloudFileItem) -> Void)?
    var onDrop: (([URL]) -> Void)?
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
    var onReceiveCloudDrop: (() -> Void)?
    var onCreateFolder: (() -> Void)?
    var onRename: ((CloudFileItem) -> Void)?
    var canCreateFolder: Bool = true
    weak var tableView: CloudTableView?
    var suppressSelectionUpdate = false
    let filePromiseDelegate = CloudFilePromiseDelegate()

    private var currentDragItems: [CloudFileItem] = []

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

        // Right-click on empty area — show "New Folder" only (if supported)
        if clickedRow < 0 || clickedRow >= items.count {
            if canCreateFolder {
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

    @objc func handleDeleteFromMenu(_ sender: NSMenuItem) {
        onDelete?()
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
        // Accept file promises from other cloud panels
        if info.draggingPasteboard.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil) {
            tableView.setDropRow(-1, dropOperation: .on)
            return .copy
        }
        // Accept file URLs from local panels
        guard info.draggingPasteboard.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) else {
            return []
        }
        tableView.setDropRow(-1, dropOperation: .on)
        return .generic
    }

    func tableView(_ tableView: NSTableView, acceptDrop info: any NSDraggingInfo, row: Int, dropOperation: NSTableView.DropOperation) -> Bool {
        // Handle file promises from other cloud panels — call immediately before drag session ends
        if info.draggingPasteboard.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil) {
            onReceiveCloudDrop?()
            return true
        }

        guard let urls = info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty else {
            return false
        }
        onDrop?(urls)
        return true
    }

    // MARK: - Cell Factories

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
}
