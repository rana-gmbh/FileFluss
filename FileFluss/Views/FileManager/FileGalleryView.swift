import AppKit
import QuickLookUI

// The gallery is the second way a panel can show a folder: a grid of tiles
// with a preview each, instead of rows. These pieces are shared by the local
// and the cloud panel; each panel's coordinator supplies the items and keeps
// every behaviour — context menu, drag and drop, Quick Look, selection — the
// same as in its list.

// MARK: - Metrics

/// Tile dimensions. They follow the same two-step size the list's rows do,
/// so ⌘+ and ⌘- make the tiles bigger and smaller too.
struct FileGalleryMetrics: Equatable {
    /// Edge of the square a preview is drawn into.
    let thumbnailSize: CGFloat
    let labelFont: NSFont
    let itemSize: NSSize
    let lineHeight: CGFloat

    /// Room around the preview for the selection box.
    static let boxPadding: CGFloat = 6
    static let topMargin: CGFloat = 4
    static let sideMargin: CGFloat = 6
    static let labelGap: CGFloat = 3
    static let pillHorizontalPadding: CGFloat = 4
    static let pillVerticalPadding: CGFloat = 1
    static let maximumLabelLines = 2

    @MainActor
    init(_ size: FileListRowSize = FileListRowSizePrefs.current) {
        switch size {
        case .regular:
            thumbnailSize = 96
            labelFont = .systemFont(ofSize: NSFont.systemFontSize - 1)
        case .large:
            thumbnailSize = 144
            labelFont = .systemFont(ofSize: NSFont.systemFontSize)
        }
        lineHeight = ceil(NSLayoutManager().defaultLineHeight(for: labelFont))
        let width = thumbnailSize + 2 * Self.boxPadding + 2 * Self.sideMargin
        let labelHeight = lineHeight * CGFloat(Self.maximumLabelLines) + 2 * Self.pillVerticalPadding
        let height = Self.topMargin + thumbnailSize + 2 * Self.boxPadding
            + Self.labelGap + labelHeight + Self.topMargin
        itemSize = NSSize(width: width, height: ceil(height))
    }
}

// MARK: - Collection view

/// The grid itself. Mirrors what the panels' table views do with keys,
/// first responder, Quick Look and the clipboard, so switching a panel to
/// the gallery changes how it looks and nothing about how it is used.
final class FileGalleryView: NSCollectionView, NSUserInterfaceValidations {
    var onSpaceKey: (() -> Void)?
    /// Called when a drag leaves or ends — the coordinator learns where a
    /// drag is from `validateDrop`, but never that it has gone, so a pending
    /// spring-load is cancelled from here.
    var onDragLeft: (() -> Void)?
    var onDelete: (() -> Void)?
    var onBecameFirstResponder: (() -> Void)?
    var onDoubleClick: ((Int) -> Void)?
    var quickLookController: QuickLookController?

    /// The item a context menu was opened on, or -1 for the background —
    /// the gallery's counterpart to NSTableView's `clickedRow`.
    private(set) var clickedIndex = -1

    /// Whether the selection is drawn in the accent colour, as a focused
    /// table draws its selected rows.
    var isEmphasized: Bool {
        guard let window else { return false }
        return window.isKeyWindow && window.firstResponder === self
    }

    /// Outlines the panel while a drop would land in the folder on screen
    /// rather than in a folder tile.
    var isBackgroundDropTarget = false {
        didSet {
            guard oldValue != isBackgroundDropTarget, let scrollView = enclosingScrollView else { return }
            scrollView.wantsLayer = true
            if isBackgroundDropTarget {
                effectiveAppearance.performAsCurrentDrawingAppearance {
                    scrollView.layer?.borderColor = NSColor.controlAccentColor.cgColor
                }
                scrollView.layer?.borderWidth = 2
            } else {
                scrollView.layer?.borderWidth = 0
            }
        }
    }

    private var pendingScrollToSelection = false
    nonisolated(unsafe) private var windowObservers: [NSObjectProtocol] = []

    deinit {
        windowObservers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    // MARK: Building

    /// A gallery in a scroll view, ready to be handed to SwiftUI.
    static func makeScrollView(
        dataSource: any NSCollectionViewDataSource,
        delegate: any NSCollectionViewDelegate,
        menuDelegate: any NSMenuDelegate,
        draggedTypes: [NSPasteboard.PasteboardType]
    ) -> (NSScrollView, FileGalleryView) {
        let gallery = FileGalleryView()
        gallery.collectionViewLayout = FileGalleryLayout()
        gallery.isSelectable = true
        gallery.allowsMultipleSelection = true
        gallery.allowsEmptySelection = true
        gallery.backgroundColors = [.controlBackgroundColor]
        gallery.register(FileGalleryItem.self, forItemWithIdentifier: FileGalleryItem.identifier)
        gallery.dataSource = dataSource
        gallery.delegate = delegate

        gallery.registerForDraggedTypes(draggedTypes)
        gallery.setDraggingSourceOperationMask(.every, forLocal: true)
        gallery.setDraggingSourceOperationMask(.every, forLocal: false)

        let menu = NSMenu()
        menu.delegate = menuDelegate
        gallery.menu = menu

        let scrollView = NSScrollView()
        scrollView.documentView = gallery
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = true
        scrollView.drawsBackground = true
        scrollView.backgroundColor = .controlBackgroundColor
        return (scrollView, gallery)
    }

    /// Applies a new tile size (⌘+ / ⌘-) and redraws the tiles with it.
    func apply(_ metrics: FileGalleryMetrics) {
        (collectionViewLayout as? FileGalleryLayout)?.apply(metrics)
        reloadData()
    }

    // MARK: Scrolling

    /// Brings the selection — or the top, with nothing selected — into view
    /// once the gallery has a size. Asked for right after a reload or while
    /// the view is still being placed, an immediate scroll would land on
    /// item frames that don't exist yet.
    func scrollToSelectionWhenReady() {
        pendingScrollToSelection = true
        needsLayout = true
    }

    override func layout() {
        super.layout()
        guard pendingScrollToSelection, visibleRect.width > 0 else { return }
        pendingScrollToSelection = false
        DispatchQueue.main.async { [weak self] in
            self?.scrollToSelection()
        }
    }

    private func scrollToSelection() {
        let count = numberOfSections > 0 ? numberOfItems(inSection: 0) : 0
        if let first = selectionIndexPaths.min(), first.item < count {
            scrollToItems(at: [first], scrollPosition: .centeredVertically)
        } else {
            scroll(.zero)
        }
    }

    // MARK: Keys

    override func keyDown(with event: NSEvent) {
        if KeyboardShortcutManager.shared.isQuickLook(event) {
            onSpaceKey?()
        } else if event.keyCode == 51 && event.modifierFlags.contains(.command) {
            onDelete?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        // As in the tables: handled here rather than through the menu, which
        // would take first responder away and leave the arrow keys dead.
        if window?.firstResponder === self,
           let command = KeyboardShortcutManager.shared.navigationCommand(for: event) {
            NotificationCenter.default.post(name: command.notification, object: nil)
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    // MARK: Focus

    override func becomeFirstResponder() -> Bool {
        let result = super.becomeFirstResponder()
        if result {
            onBecameFirstResponder?()
            refreshSelectionAppearance()
        }
        return result
    }

    override func resignFirstResponder() -> Bool {
        let result = super.resignFirstResponder()
        if result { refreshSelectionAppearance() }
        return result
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        windowObservers.forEach { NotificationCenter.default.removeObserver($0) }
        windowObservers = []
        guard let window else { return }
        let refresh: @Sendable (Notification) -> Void = { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshSelectionAppearance() }
        }
        let nc = NotificationCenter.default
        windowObservers = [
            nc.addObserver(forName: NSWindow.didBecomeKeyNotification, object: window, queue: .main, using: refresh),
            nc.addObserver(forName: NSWindow.didResignKeyNotification, object: window, queue: .main, using: refresh),
        ]
    }

    func refreshSelectionAppearance() {
        for item in visibleItems() {
            (item as? FileGalleryItem)?.updateAppearance()
        }
    }

    // MARK: Mouse

    override func mouseDown(with event: NSEvent) {
        onBecameFirstResponder?()
        super.mouseDown(with: event)
        guard event.clickCount == 2 else { return }
        let point = convert(event.locationInWindow, from: nil)
        if let indexPath = indexPathForItem(at: point) {
            onDoubleClick?(indexPath.item)
        }
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        clickedIndex = indexPathForItem(at: point)?.item ?? -1
        onBecameFirstResponder?()
        return super.menu(for: event)
    }

    override func didCloseMenu(_ menu: NSMenu, with event: NSEvent?) {
        super.didCloseMenu(menu, with: event)
        clickedIndex = -1
    }

    // MARK: Drag destination

    override func draggingExited(_ sender: (any NSDraggingInfo)?) {
        onDragLeft?()
        isBackgroundDropTarget = false
        super.draggingExited(sender)
    }

    override func draggingEnded(_ sender: any NSDraggingInfo) {
        onDragLeft?()
        isBackgroundDropTarget = false
        // Passed on only where the superclass has it: the tables crashed on
        // exactly this call because NSTableView doesn't implement the
        // optional method (see FileTableView.draggingEnded). NSCollectionView
        // does, and clears its own drop highlight there.
        if NSCollectionView.instancesRespond(to: #selector(NSDraggingDestination.draggingEnded(_:))) {
            super.draggingEnded(sender)
        }
    }

    // MARK: QLPreviewPanel support

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

    // MARK: Responder-chain Cut / Copy / Paste
    //
    // See FileTableView: ⌘C/⌘X/⌘V reach the first responder before any menu
    // key equivalent, so they are answered here and posted as the same
    // commands the menu items send.

    @objc func copy(_ sender: Any?) {
        NotificationCenter.default.post(name: KeyboardCommand.copyFiles.notification, object: nil)
    }

    @objc func cut(_ sender: Any?) {
        NotificationCenter.default.post(name: KeyboardCommand.cutFiles.notification, object: nil)
    }

    @objc func paste(_ sender: Any?) {
        NotificationCenter.default.post(name: KeyboardCommand.pasteFiles.notification, object: nil)
    }

    /// NSCollectionView validates its own actions in a method the SDK
    /// doesn't declare, so this replaces rather than extends it; what it
    /// would have checked (Select All) is enabled either way.
    func validateUserInterfaceItem(_ item: any NSValidatedUserInterfaceItem) -> Bool {
        switch item.action {
        case #selector(copy(_:)), #selector(cut(_:)):
            return !selectionIndexPaths.isEmpty
        default:
            return true
        }
    }
}

// MARK: - Layout

/// A flow layout whose drops land in folders, not between tiles. A drop in a
/// file manager goes into a folder; there is no position in the listing to
/// point at, so the gap indicator a flow layout draws would only mislead.
final class FileGalleryLayout: NSCollectionViewFlowLayout {
    override init() {
        super.init()
        minimumInteritemSpacing = 4
        minimumLineSpacing = 8
        sectionInset = NSEdgeInsets(top: 10, left: 10, bottom: 10, right: 10)
        apply(FileGalleryMetrics())
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
    }

    func apply(_ metrics: FileGalleryMetrics) {
        itemSize = metrics.itemSize
        invalidateLayout()
    }

    /// The tile under the cursor, or else "after the last tile", which the
    /// coordinators read as the folder on screen.
    override func layoutAttributesForDropTarget(at pointInCollectionView: NSPoint) -> NSCollectionViewLayoutAttributes? {
        let probe = NSRect(x: pointInCollectionView.x, y: pointInCollectionView.y, width: 1, height: 1)
        if let hit = layoutAttributesForElements(in: probe).first(where: {
            $0.representedElementCategory == .item && $0.frame.contains(pointInCollectionView)
        }) {
            return hit
        }
        let count = (collectionView?.numberOfSections ?? 0) > 0
            ? collectionView?.numberOfItems(inSection: 0) ?? 0
            : 0
        let gap = NSCollectionViewLayoutAttributes(forInterItemGapBefore: IndexPath(item: count, section: 0))
        gap.isHidden = true
        return gap
    }

    override func layoutAttributesForInterItemGap(before indexPath: IndexPath) -> NSCollectionViewLayoutAttributes? {
        nil
    }
}

// MARK: - Item

/// One tile. Keeps track of which listing entry it shows, so a thumbnail
/// that arrives after the tile was reused for another entry is dropped.
final class FileGalleryItem: NSCollectionViewItem {
    static let identifier = NSUserInterfaceItemIdentifier("FileGalleryItem")

    private(set) var representedID: String?
    /// The thumbnail being generated for the entry shown, withdrawn when the
    /// tile is reused — scrolling past a thousand photos must not leave a
    /// thousand previews queued ahead of the ones on screen.
    var thumbnailTask: Task<Void, Never>? {
        willSet { thumbnailTask?.cancel() }
    }

    private var tile: FileGalleryTileView {
        view as! FileGalleryTileView
    }

    override func loadView() {
        view = FileGalleryTileView()
    }

    override var isSelected: Bool {
        didSet { updateAppearance() }
    }

    override var highlightState: NSCollectionViewItem.HighlightState {
        didSet { updateAppearance() }
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        thumbnailTask = nil
        representedID = nil
    }

    /// Shows an entry with its icon; a thumbnail may replace the icon later.
    func configure(
        id: String,
        name: String,
        toolTip: String,
        icon: NSImage,
        iconTint: NSColor? = nil,
        badge: (image: NSImage, tint: NSColor)? = nil,
        metrics: FileGalleryMetrics
    ) {
        representedID = id
        tile.metrics = metrics
        tile.label.stringValue = name
        tile.toolTip = toolTip
        tile.imageView.image = icon
        tile.imageView.contentTintColor = iconTint
        tile.badgeView.image = badge?.image
        tile.badgeView.contentTintColor = badge?.tint
        tile.badgeView.isHidden = badge == nil
        tile.needsLayout = true
        updateAppearance()
    }

    func showThumbnail(_ image: NSImage) {
        tile.imageView.contentTintColor = nil
        tile.imageView.image = image
    }

    func updateAppearance() {
        let selected = (isSelected && highlightState != .forDeselection) || highlightState == .forSelection
        let emphasized = (collectionView as? FileGalleryView)?.isEmphasized ?? false
        tile.apply(selected: selected, emphasized: emphasized, dropTarget: highlightState == .asDropTarget)
    }
}

// MARK: - Tile view

/// Preview on top, name below — two lines at most, as in Finder. Laid out
/// by hand: every tile has the same fixed geometry, and Auto Layout would
/// only add work to every scroll.
final class FileGalleryTileView: NSView {
    let imageBox = NSView()
    let imageView = NSImageView()
    let labelBackground = NSView()
    let label = NSTextField(wrappingLabelWithString: "")
    let badgeView = NSImageView()

    var metrics = FileGalleryMetrics() {
        didSet {
            guard metrics != oldValue else { return }
            label.font = metrics.labelFont
            badgeView.symbolConfiguration = NSImage.SymbolConfiguration(
                pointSize: max(12, metrics.thumbnailSize / 7), weight: .semibold
            )
            needsLayout = true
        }
    }

    private var selected = false
    private var emphasized = false
    private var dropTarget = false

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true

        imageBox.wantsLayer = true
        imageBox.layer?.cornerRadius = 6

        imageView.imageScaling = .scaleProportionallyUpOrDown
        imageView.imageAlignment = .alignCenter

        labelBackground.wantsLayer = true
        labelBackground.layer?.cornerRadius = 4

        label.font = metrics.labelFont
        label.alignment = .center
        label.lineBreakMode = .byWordWrapping
        label.maximumNumberOfLines = FileGalleryMetrics.maximumLabelLines
        label.cell?.truncatesLastVisibleLine = true
        label.isSelectable = false
        label.drawsBackground = false

        badgeView.symbolConfiguration = NSImage.SymbolConfiguration(
            pointSize: max(12, metrics.thumbnailSize / 7), weight: .semibold
        )
        badgeView.isHidden = true

        addSubview(imageBox)
        addSubview(imageView)
        addSubview(labelBackground)
        addSubview(label)
        addSubview(badgeView)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override var isFlipped: Bool { true }

    /// Every click goes to the gallery: it decides selection, drags and
    /// rubber-band selection from where the click landed, and a label or
    /// image view in between would only get in its way.
    override func hitTest(_ point: NSPoint) -> NSView? {
        nil
    }

    override func layout() {
        super.layout()
        let m = metrics
        let boxSide = m.thumbnailSize + 2 * FileGalleryMetrics.boxPadding
        let box = NSRect(
            x: ((bounds.width - boxSide) / 2).rounded(.down),
            y: FileGalleryMetrics.topMargin,
            width: boxSide,
            height: boxSide
        )
        imageBox.frame = box
        imageView.frame = box.insetBy(dx: FileGalleryMetrics.boxPadding, dy: FileGalleryMetrics.boxPadding)

        let badgeSide = max(14, m.thumbnailSize / 5)
        badgeView.frame = NSRect(
            x: imageView.frame.maxX - badgeSide,
            y: imageView.frame.maxY - badgeSide,
            width: badgeSide,
            height: badgeSide
        )

        let hPad: CGFloat = FileGalleryMetrics.pillHorizontalPadding
        let vPad: CGFloat = FileGalleryMetrics.pillVerticalPadding
        let maxTextWidth: CGFloat = max(0, bounds.width - 2 * hPad - 2)
        let maxTextHeight: CGFloat = m.lineHeight * CGFloat(FileGalleryMetrics.maximumLabelLines)
        let measureBounds = NSRect(x: 0, y: 0, width: maxTextWidth, height: .greatestFiniteMagnitude)
        let measured: NSSize = label.cell?.cellSize(forBounds: measureBounds) ?? .zero
        let textWidth: CGFloat = min(ceil(measured.width), maxTextWidth)
        let textHeight: CGFloat = min(ceil(measured.height), maxTextHeight)
        let pillWidth: CGFloat = textWidth + 2 * hPad
        let pillHeight: CGFloat = textHeight + 2 * vPad
        let pillX: CGFloat = ((bounds.width - pillWidth) / 2).rounded(.down)
        let pillY: CGFloat = box.maxY + FileGalleryMetrics.labelGap
        let pill = NSRect(x: pillX, y: pillY, width: pillWidth, height: pillHeight)
        labelBackground.frame = pill
        label.frame = pill.insetBy(dx: hPad, dy: vPad)
    }

    func apply(selected: Bool, emphasized: Bool, dropTarget: Bool) {
        guard selected != self.selected || emphasized != self.emphasized || dropTarget != self.dropTarget else { return }
        self.selected = selected
        self.emphasized = emphasized
        self.dropTarget = dropTarget
        updateColors()
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        updateColors()
    }

    private func updateColors() {
        effectiveAppearance.performAsCurrentDrawingAppearance {
            if dropTarget {
                imageBox.layer?.backgroundColor = NSColor.controlAccentColor.withAlphaComponent(0.2).cgColor
                imageBox.layer?.borderColor = NSColor.controlAccentColor.cgColor
                imageBox.layer?.borderWidth = 2
            } else {
                imageBox.layer?.backgroundColor = selected
                    ? NSColor.labelColor.withAlphaComponent(0.1).cgColor
                    : NSColor.clear.cgColor
                imageBox.layer?.borderWidth = 0
            }

            if selected {
                labelBackground.layer?.backgroundColor = emphasized
                    ? NSColor.selectedContentBackgroundColor.cgColor
                    : NSColor.unemphasizedSelectedContentBackgroundColor.cgColor
                label.textColor = emphasized ? .alternateSelectedControlTextColor : .labelColor
            } else {
                labelBackground.layer?.backgroundColor = NSColor.clear.cgColor
                label.textColor = .labelColor
            }
        }
    }
}
