import Testing
import AppKit
@testable import FileFluss

/// Each panel can show its folder as a list or as a gallery, and keeps the
/// choice between launches. List is the default.
@Suite("View mode preference")
struct FileViewModePrefsTests {

    /// A private defaults domain: a test has no business writing the app's
    /// own preferences.
    private func freshDefaults() -> UserDefaults {
        let name = "FileFlussTests.viewMode.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defaults.removePersistentDomain(forName: name)
        return defaults
    }

    @Test("a panel nobody switched shows a list")
    func defaultsToList() {
        let defaults = freshDefaults()
        #expect(FileViewModePrefs.load(for: .left, from: defaults) == .list)
        #expect(FileViewModePrefs.load(for: .right, from: defaults) == .list)
    }

    @Test("each panel remembers its own choice")
    func remembersPerPanel() {
        let defaults = freshDefaults()
        FileViewModePrefs.save(.gallery, for: .right, to: defaults)
        #expect(FileViewModePrefs.load(for: .right, from: defaults) == .gallery)
        #expect(FileViewModePrefs.load(for: .left, from: defaults) == .list)

        FileViewModePrefs.save(.list, for: .right, to: defaults)
        #expect(FileViewModePrefs.load(for: .right, from: defaults) == .list)
    }

    @Test("a value from some other version falls back to the list")
    func unknownValueIsList() {
        let defaults = freshDefaults()
        defaults.set("columns", forKey: FileViewModePrefs.storageKey(for: .left))
        #expect(FileViewModePrefs.load(for: .left, from: defaults) == .list)
    }
}

/// The menu switches between the two modes with shortcuts of their own; a
/// default that collides with another command would silently steal it.
@Suite("View mode shortcuts")
@MainActor
struct ViewModeShortcutTests {

    @Test("both presets bind the view modes, to keys nothing else uses")
    func noClashes() {
        for defaults in [KeyboardShortcutManager.finderDefaults, KeyboardShortcutManager.commanderDefaults] {
            for command in [KeyboardCommand.viewAsList, .viewAsGallery] {
                guard let binding = defaults[command] else {
                    Issue.record("\(command) has no default shortcut")
                    continue
                }
                let others = defaults.filter { $0.key != command && $0.value == binding }
                #expect(others.isEmpty, "\(command) clashes with \(others.map(\.key))")
            }
        }
    }
}

/// The gallery's tiles and drops.
@Suite("Gallery")
@MainActor
struct GalleryTests {

    private final class StubSource: NSObject, NSCollectionViewDataSource {
        let count: Int
        init(count: Int) { self.count = count }

        func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int {
            count
        }

        func collectionView(_ collectionView: NSCollectionView, itemForRepresentedObjectAt indexPath: IndexPath) -> NSCollectionViewItem {
            collectionView.makeItem(withIdentifier: FileGalleryItem.identifier, for: indexPath)
        }
    }

    @Test("larger tiles with ⌘+, with room for two lines of name")
    func tileSizes() {
        let regular = FileGalleryMetrics(.regular)
        let large = FileGalleryMetrics(.large)
        #expect(large.thumbnailSize > regular.thumbnailSize)
        #expect(large.itemSize.width > regular.itemSize.width)
        #expect(regular.itemSize.height >= regular.thumbnailSize + 2 * regular.lineHeight)
    }

    /// A drop in a file manager goes into a folder: onto a tile means that
    /// tile, anywhere else means the folder on screen — which the
    /// coordinators read from "before the end of the list".
    @Test("a drop lands on the tile under the cursor, or else in the folder")
    func dropTargets() throws {
        let source = StubSource(count: 3)
        let frame = NSRect(x: 0, y: 0, width: 800, height: 600)
        let gallery = FileGalleryView(frame: frame)
        let layout = FileGalleryLayout()
        gallery.collectionViewLayout = layout
        gallery.register(FileGalleryItem.self, forItemWithIdentifier: FileGalleryItem.identifier)
        gallery.dataSource = source
        // Hosted the way the app hosts it, in a scroll view in a window:
        // without one a collection view never lays its items out.
        let scrollView = NSScrollView(frame: frame)
        scrollView.documentView = gallery
        let window = NSWindow(contentRect: frame, styleMask: [.titled], backing: .buffered, defer: true)
        window.contentView = scrollView
        gallery.reloadData()
        window.layoutIfNeeded()
        gallery.layoutSubtreeIfNeeded()

        let first = try #require(layout.layoutAttributesForItem(at: IndexPath(item: 0, section: 0)))
        let onTile = layout.layoutAttributesForDropTarget(at: NSPoint(x: first.frame.midX, y: first.frame.midY))
        #expect(onTile?.representedElementCategory == .item)
        #expect(onTile?.indexPath == IndexPath(item: 0, section: 0))

        let empty = layout.layoutAttributesForDropTarget(at: NSPoint(x: 790, y: 590))
        #expect(empty?.representedElementCategory == .interItemGap)
        #expect(empty?.indexPath == IndexPath(item: 3, section: 0))
        #expect(layout.layoutAttributesForInterItemGap(before: IndexPath(item: 1, section: 0)) == nil)
    }
}
