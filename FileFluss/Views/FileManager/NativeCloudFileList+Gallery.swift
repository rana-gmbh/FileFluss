import AppKit
import FileFlussCore

// The cloud panel's gallery. As in the local one, everything a tile can do
// runs through the coordinator methods the list uses. Cloud files have no
// local copy for Quick Look to draw from, so a tile shows the type's icon
// until `CloudThumbnailLoader` brings a preview.

extension NativeCloudFileList {
    func makeGallery(coordinator: CloudTableCoordinator) -> NSScrollView {
        let (scrollView, gallery) = FileGalleryView.makeScrollView(
            dataSource: coordinator,
            delegate: coordinator,
            menuDelegate: coordinator,
            draggedTypes: [.fileURL, .filePromise]
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

extension CloudTableCoordinator: NSCollectionViewDataSource, NSCollectionViewDelegate {

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

    private func configure(_ tile: FileGalleryItem, with item: CloudFileItem) {
        let metrics = galleryMetrics ?? FileGalleryMetrics()
        let iconSize = metrics.thumbnailSize

        let icon: NSImage
        var tint: NSColor?
        if let symbol = item.symbolIconOverride,
           let symbolImage = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?
               .withSymbolConfiguration(.init(pointSize: iconSize * 0.55, weight: .regular)) {
            // Synthetic entries (e.g. Google Drive "Shared drives") read as
            // drives rather than ordinary folders, as in the list.
            icon = symbolImage
            tint = .secondaryLabelColor
        } else if item.isDirectory {
            icon = FileTypeIcon.folderIcon(pointSize: iconSize)
        } else {
            icon = FileTypeIcon.icon(forFilename: item.name, pointSize: iconSize)
        }

        var badge: (image: NSImage, tint: NSColor)?
        switch item.downloadStatus {
        case .local:
            badge = nil
        case .evicted:
            if let image = NSImage(systemSymbolName: "icloud.and.arrow.down", accessibilityDescription: "Not downloaded") {
                badge = (image, .secondaryLabelColor)
            }
        case .downloading:
            if let image = NSImage(systemSymbolName: "arrow.down.circle.fill", accessibilityDescription: "Downloading") {
                badge = (image, .controlAccentColor)
            }
        }

        tile.configure(
            id: item.id,
            name: displayedName(for: item),
            toolTip: item.name,
            icon: icon,
            iconTint: tint,
            badge: badge,
            metrics: metrics
        )
        loadThumbnail(into: tile, for: item, pointSize: iconSize)
    }

    /// Replaces the type icon with a preview from the service, or from a
    /// small download, once it arrives. A tile reused for another file
    /// drops the late preview and withdraws the request.
    private func loadThumbnail(into tile: FileGalleryItem, for item: CloudFileItem, pointSize: CGFloat) {
        guard let accountId = thumbnailAccountId, !archiveMode,
              CloudThumbnailLoader.wantsThumbnail(for: item) else { return }
        let cache = ThumbnailCache.shared
        let identity = "cloud:\(accountId.uuidString)\(item.path)"
        if let thumb = cache.cached(identity: identity, mtime: item.modificationDate, size: item.size, pointSize: pointSize) {
            tile.showThumbnail(thumb)
            return
        }
        let id = item.id
        tile.thumbnailTask = Task { @MainActor [weak tile] in
            let thumb = await cache.thumbnail(
                identity: identity,
                mtime: item.modificationDate,
                size: item.size,
                pointSize: pointSize
            ) { pixels in
                await CloudThumbnailLoader.shared.thumbnail(accountId: accountId, item: item, pixels: pixels)
            }
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
