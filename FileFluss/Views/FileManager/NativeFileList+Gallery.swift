import AppKit
import FileFlussCore

// The local panel's gallery. Everything a tile can do — the context menu,
// drag and drop, spring-loaded folders — goes through the same coordinator
// methods the list uses, so the two views can't drift apart.

extension NativeFileList {
    func makeGallery(coordinator: FileTableCoordinator) -> NSScrollView {
        let promiseTypes = NSFilePromiseReceiver.readableDraggedTypes.map { NSPasteboard.PasteboardType($0) }
        let (scrollView, gallery) = FileGalleryView.makeScrollView(
            dataSource: coordinator,
            delegate: coordinator,
            menuDelegate: coordinator,
            draggedTypes: [.fileURL] + promiseTypes
        )
        gallery.onSpaceKey = { [weak coordinator] in coordinator?.onKeySpace?() }
        gallery.onDelete = { [weak coordinator] in coordinator?.onDelete?() }
        gallery.onBecameFirstResponder = { [weak coordinator] in coordinator?.onBecameActive?() }
        gallery.onDragLeft = { [weak coordinator] in coordinator?.cancelSpringLoad() }
        gallery.onDoubleClick = { [weak coordinator] index in
            guard let coordinator, index < coordinator.items.count else { return }
            coordinator.onDoubleClick?(coordinator.items[index])
        }
        if let quickLookController {
            gallery.quickLookController = quickLookController
            quickLookController.sourceView = gallery
        }
        coordinator.galleryMetrics = FileGalleryMetrics()
        coordinator.galleryView = gallery
        return scrollView
    }
}

extension FileTableCoordinator: NSCollectionViewDataSource, NSCollectionViewDelegate {

    // MARK: Updates from SwiftUI

    func updateGallery(
        _ galleryView: FileGalleryView,
        itemsChanged: Bool,
        selectedIDs: Set<String>,
        location: String,
        focusToken: UUID?
    ) {
        if itemsChanged {
            galleryView.reloadData()
        }

        // Selection SwiftUI → gallery, only when they differ.
        let shown = Set(galleryView.selectionIndexPaths.compactMap { path -> String? in
            path.item < items.count ? items[path.item].id : nil
        })
        if shown != selectedIDs {
            var paths = Set<IndexPath>()
            for (index, item) in items.enumerated() where selectedIDs.contains(item.id) {
                paths.insert(IndexPath(item: index, section: 0))
            }
            suppressSelectionUpdate = true
            galleryView.selectionIndexPaths = paths
            suppressSelectionUpdate = false
        }

        // A new folder starts at the top — or at the folder just left, when
        // going up — like the list does.
        if lastGalleryLocation != location {
            lastGalleryLocation = location
            galleryView.scrollToSelectionWhenReady()
        }

        if let focusToken, focusToken != lastFocusToken {
            lastFocusToken = focusToken
            focusTableViewSoon()
        }
    }

    func gallerySelectionDidChange() {
        guard !suppressSelectionUpdate, let galleryView else { return }
        let newSelection = Set(galleryView.selectionIndexPaths.compactMap { path -> String? in
            path.item < items.count ? items[path.item].id : nil
        })
        if selectedIDs?.wrappedValue != newSelection {
            selectedIDs?.wrappedValue = newSelection
        }
    }

    // MARK: Data source

    func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int {
        items.count
    }

    func collectionView(_ collectionView: NSCollectionView, itemForRepresentedObjectAt indexPath: IndexPath) -> NSCollectionViewItem {
        let tile = collectionView.makeItem(withIdentifier: FileGalleryItem.identifier, for: indexPath)
        if let tile = tile as? FileGalleryItem, indexPath.item < items.count {
            configure(tile, with: items[indexPath.item])
        }
        return tile
    }

    private func configure(_ tile: FileGalleryItem, with item: FileItem) {
        let metrics = galleryMetrics ?? FileGalleryMetrics()
        let iconSize = metrics.thumbnailSize
        let icon = item.isDirectory
            ? FileTypeIcon.folderIcon(pointSize: iconSize)
            : FileTypeIcon.icon(for: item.contentType, pointSize: iconSize)
        tile.configure(
            id: item.id,
            name: displayedName(for: item),
            toolTip: item.name,
            icon: icon,
            metrics: metrics
        )

        // Inside an archive there is no file on disk for Quick Look to read.
        guard !archiveMode, !item.isDirectory,
              ThumbnailCache.shouldUseGalleryThumbnail(for: item.contentType) else { return }
        let cache = ThumbnailCache.shared
        if let thumb = cache.cached(for: item.url, mtime: item.modificationDate, size: item.size, pointSize: iconSize) {
            tile.showThumbnail(thumb)
            return
        }
        let id = item.id
        let url = item.url
        let mtime = item.modificationDate
        let size = item.size
        tile.thumbnailTask = Task { @MainActor [weak tile] in
            let thumb = await cache.thumbnail(for: url, mtime: mtime, size: size, pointSize: iconSize)
            guard let thumb, !Task.isCancelled, let tile, tile.representedID == id else { return }
            tile.showThumbnail(thumb)
        }
    }

    // MARK: Selection

    func collectionView(_ collectionView: NSCollectionView, didSelectItemsAt indexPaths: Set<IndexPath>) {
        gallerySelectionDidChange()
    }

    func collectionView(_ collectionView: NSCollectionView, didDeselectItemsAt indexPaths: Set<IndexPath>) {
        gallerySelectionDidChange()
    }

    // MARK: Drag source

    func collectionView(_ collectionView: NSCollectionView, canDragItemsAt indexPaths: Set<IndexPath>, with event: NSEvent) -> Bool {
        true
    }

    func collectionView(_ collectionView: NSCollectionView, pasteboardWriterForItemAt indexPath: IndexPath) -> (any NSPasteboardWriting)? {
        pasteboardWriter(at: indexPath.item)
    }

    func collectionView(_ collectionView: NSCollectionView, draggingSession session: NSDraggingSession, willBeginAt screenPoint: NSPoint, forItemsAt indexPaths: Set<IndexPath>) {
        dragBegan(at: IndexSet(indexPaths.map(\.item)))
    }

    func collectionView(_ collectionView: NSCollectionView, draggingSession session: NSDraggingSession, endedAt screenPoint: NSPoint, dragOperation operation: NSDragOperation) {
        dragEnded()
    }

    // MARK: Drop destination

    func collectionView(
        _ collectionView: NSCollectionView,
        validateDrop draggingInfo: any NSDraggingInfo,
        proposedIndexPath proposedDropIndexPath: AutoreleasingUnsafeMutablePointer<NSIndexPath>,
        dropOperation proposedDropOperation: UnsafeMutablePointer<NSCollectionView.DropOperation>
    ) -> NSDragOperation {
        let onIndex = proposedDropOperation.pointee == .on ? proposedDropIndexPath.pointee.item : nil
        let target = dropTarget(for: draggingInfo, onItemAt: onIndex)
        return FileGalleryDrop.apply(
            target,
            to: collectionView,
            itemCount: items.count,
            proposedIndexPath: proposedDropIndexPath,
            dropOperation: proposedDropOperation
        )
    }

    func collectionView(_ collectionView: NSCollectionView, acceptDrop draggingInfo: any NSDraggingInfo, indexPath: IndexPath, dropOperation: NSCollectionView.DropOperation) -> Bool {
        (collectionView as? FileGalleryView)?.isBackgroundDropTarget = false
        return performDrop(draggingInfo, onItemAt: dropOperation == .on ? indexPath.item : nil)
    }
}

/// Turns a coordinator's verdict into what a collection view understands.
enum FileGalleryDrop {
    @MainActor
    static func apply(
        _ target: FileDropTarget,
        to collectionView: NSCollectionView,
        itemCount: Int,
        proposedIndexPath: AutoreleasingUnsafeMutablePointer<NSIndexPath>,
        dropOperation: UnsafeMutablePointer<NSCollectionView.DropOperation>
    ) -> NSDragOperation {
        let gallery = collectionView as? FileGalleryView
        switch target {
        case .refuse:
            gallery?.isBackgroundDropTarget = false
            return []
        case .item(let operation):
            gallery?.isBackgroundDropTarget = false
            return operation
        case .background(let operation):
            // "Before the end" is what the coordinators read as the folder on
            // screen; the layout draws no gap indicator for it.
            proposedIndexPath.pointee = NSIndexPath(forItem: itemCount, inSection: 0)
            dropOperation.pointee = .before
            gallery?.isBackgroundDropTarget = true
            return operation
        }
    }
}
