import AppKit

/// Resolves a filename extension to a bundled colorful file-type icon
/// from the free-file-icons set (MIT, Teambox / redbooth — see About).
/// Returns nil for extensions we don't have an icon for; callers should
/// fall back to NSWorkspace's system icon.
enum FreeFileIcon {
    private static let assetNamespace = "FileTypeIcons/"

    /// Map common variants and modern formats onto the available 2009-era
    /// icon set so e.g. `.docx` reuses the `doc` icon, `.jpeg` reuses
    /// `jpg`, etc.
    private static let extensionAlias: [String: String] = [
        "docx": "doc",
        "xlsm": "xls",
        "xlsb": "xls",
        "pptx": "ppt",
        "pps": "ppt",
        "ppsx": "ppt",
        "jpeg": "jpg",
        "htm": "html",
        "yaml": "yml",
        "tar": "tgz",
        "gz": "tgz",
        "7z": "zip",
        "m4a": "aac",
        "ogg": "mp3",
        "oga": "mp3",
        "wma": "mp3",
        "wmv": "avi",
        "webm": "mp4",
        "mkv": "mp4",
        "mov": "qt",
        "log": "txt",
        "md": "txt",
        "markdown": "txt",
        "ini": "txt",
        "cfg": "txt",
        "conf": "txt",
        "ts": "js",
        "tsx": "js",
        "jsx": "js",
        "json": "js",
        "kt": "java",
        "scala": "java",
        "go": "c",
        "rs": "c",
        "swift": "c",
        "m": "c",
        "mm": "cpp",
        "hxx": "hpp",
        "hh": "hpp",
        "cxx": "cpp",
        "cc": "cpp",
        "tex": "txt",
        "epub": "pdf",
    ]

    /// Returns a colorful icon for the given filename, or nil if the
    /// extension isn't covered.
    static func icon(forFilename name: String) -> NSImage? {
        let ext = (name as NSString).pathExtension.lowercased()
        guard !ext.isEmpty else { return nil }
        let asset = extensionAlias[ext] ?? ext
        return NSImage(named: assetNamespace + asset)
    }
}
