import SwiftUI

/// Curated list of SF Symbols offered in the "Change Icon" submenu for
/// sidebar favorites. Includes the system defaults (Home / Documents /
/// etc.) so the user can rebuild a removed default if they want it back,
/// plus a handful of broadly useful glyphs.
enum FavoriteIconLibrary {
    struct Item: Hashable {
        let symbol: String
        let name: String
    }

    static let allSymbols: [Item] = [
        Item(symbol: "house", name: "Home"),
        Item(symbol: "menubar.dock.rectangle", name: "Desktop"),
        Item(symbol: "doc", name: "Document"),
        Item(symbol: "doc.text", name: "Text Document"),
        Item(symbol: "arrow.down.circle", name: "Downloads"),
        Item(symbol: "photo", name: "Pictures"),
        Item(symbol: "music.note", name: "Music"),
        Item(symbol: "film", name: "Movies"),
        Item(symbol: "folder.fill", name: "Folder"),
        Item(symbol: "cloud.fill", name: "Cloud"),
        Item(symbol: "star.fill", name: "Star"),
        Item(symbol: "heart.fill", name: "Heart"),
        Item(symbol: "flag.fill", name: "Flag"),
        Item(symbol: "bookmark.fill", name: "Bookmark"),
        Item(symbol: "tag.fill", name: "Tag"),
        Item(symbol: "briefcase.fill", name: "Work"),
        Item(symbol: "archivebox.fill", name: "Archive"),
        Item(symbol: "tray.fill", name: "Inbox"),
        Item(symbol: "paperplane.fill", name: "Sent"),
        Item(symbol: "person.crop.circle.fill", name: "Personal"),
    ]
}

/// Renders a SidebarFavorite icon string — either an SF Symbol name
/// (the default) or an asset-catalog name prefixed with `@asset:`.
/// Lets users assign their cloud provider's logo to a favorite while
/// keeping the existing SF Symbol pipeline for everything else.
struct FavoriteIconView: View {
    let icon: String

    private static let assetPrefix = "@asset:"

    var body: some View {
        if icon.hasPrefix(Self.assetPrefix) {
            let asset = String(icon.dropFirst(Self.assetPrefix.count))
            Image(asset)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: 16, height: 16)
        } else {
            Image(systemName: icon)
        }
    }
}

extension String {
    /// Wraps an asset-catalog image name in the prefix the
    /// `SidebarFavorite.icon` field expects.
    static func favoriteAssetIcon(_ assetName: String) -> String {
        "@asset:\(assetName)"
    }
}
