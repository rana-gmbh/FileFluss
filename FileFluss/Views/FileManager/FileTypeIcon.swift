import AppKit
import UniformTypeIdentifiers

/// Cached Finder-style icons (NSWorkspace.shared.icon) for use in file
/// list cells. NSWorkspace.icon hits LaunchServices on every call, so we
/// cache by UTType identifier / extension to keep scrolling smooth.
///
/// Since macOS 26 the images NSWorkspace returns are backed by lazy
/// IconServices reps that render asynchronously per size / scale /
/// appearance: each fresh draw (cell reuse, reloadData, selection
/// highlight, window focus change) can paint blank first and fill in a
/// frame later, which shows up as flickering icons in the file lists.
/// We rasterize every icon once into plain bitmap reps so drawing is
/// synchronous and stable. Bitmaps don't adapt to appearance changes on
/// their own, so the cache is keyed by the effective appearance.
@MainActor
enum FileTypeIcon {
    private static var cache: [String: NSImage] = [:]

    /// Point size the icons are rasterized at. Big enough for the 18pt
    /// list cells and the search popup, with @1x and @2x reps.
    private static let rasterPointSize: CGFloat = 32

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
        let appearance = NSApp.effectiveAppearance
        let appearanceKey = appearance.bestMatch(from: [.darkAqua, .aqua])?.rawValue ?? "aqua"
        let fullKey = "\(appearanceKey)|\(key)"
        if let cached = cache[fullKey] { return cached }
        var img = build()
        appearance.performAsCurrentDrawingAppearance {
            img = rasterized(img)
        }
        cache[fullKey] = img
        return img
    }

    /// Renders `source` into static bitmap reps at @1x and @2x. Falls back
    /// to the original image if rendering fails.
    private static func rasterized(_ source: NSImage) -> NSImage {
        let size = NSSize(width: rasterPointSize, height: rasterPointSize)
        let result = NSImage(size: size)
        for scale in [1, 2] {
            let px = Int(rasterPointSize) * scale
            guard let rep = NSBitmapImageRep(
                bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px,
                bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
            ), let context = NSGraphicsContext(bitmapImageRep: rep) else { continue }
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = context
            context.imageInterpolation = .high
            source.draw(in: NSRect(x: 0, y: 0, width: px, height: px),
                        from: .zero, operation: .copy, fraction: 1)
            NSGraphicsContext.restoreGraphicsState()
            rep.size = size
            result.addRepresentation(rep)
        }
        return result.representations.isEmpty ? source : result
    }
}
