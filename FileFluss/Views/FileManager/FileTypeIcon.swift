import AppKit
import UniformTypeIdentifiers

/// Cached Finder-style icons (NSWorkspace.shared.icon) for use in file
/// list cells. NSWorkspace.icon hits LaunchServices on every call, so we
/// cache by UTType identifier / extension to keep scrolling smooth.
@MainActor
enum FileTypeIcon {
    private static var cache: [String: NSImage] = [:]

    static func folderIcon() -> NSImage {
        cached(key: "__folder") { NSWorkspace.shared.icon(for: .folder) }
    }

    static func icon(for type: UTType?) -> NSImage {
        let utType = type ?? .data
        return cached(key: utType.identifier) {
            NSWorkspace.shared.icon(for: utType)
        }
    }

    /// For items that only know their filename (cloud listings), resolve
    /// the extension to a UTType and cache the resulting icon.
    static func icon(forFilename name: String) -> NSImage {
        let ext = (name as NSString).pathExtension.lowercased()
        guard !ext.isEmpty else { return icon(for: nil) }
        return cached(key: "ext:\(ext)") {
            let utType = UTType(filenameExtension: ext) ?? .data
            return NSWorkspace.shared.icon(for: utType)
        }
    }

    private static func cached(key: String, build: () -> NSImage) -> NSImage {
        if let cached = cache[key] { return cached }
        let img = build()
        cache[key] = img
        return img
    }
}
