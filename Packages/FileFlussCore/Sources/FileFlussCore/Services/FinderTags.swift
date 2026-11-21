import Foundation

/// Reads and writes Finder's tags on local files.
///
/// Finder keeps them in one extended attribute, a property list holding an
/// array of `"Name\nColourIndex"` strings. That is read and written here
/// directly rather than through `URLResourceValues.tagNames`, for three
/// reasons found by testing on macOS 27:
///
/// - `tagNames` gives the names without their colours.
/// - Setting `tagNames` to a standard name can lose its colour ("Gray"
///   came back with index 0).
/// - Asking a directory listing for `.tagNamesKey` triples the time the
///   listing takes. A separate pass over the attribute costs about 18 µs a
///   file and can run after the rows are on screen.
///
/// Every call here blocks on the file system, so none may run on the main
/// thread except for a single file the user has just acted on.
public enum FinderTags {
    static let attributeName = "com.apple.metadata:_kMDItemUserTags"

    // MARK: - Encoding

    /// `"Red\n6"` → Red, red. A missing or unknown index means no colour.
    ///
    /// A standard colour tag takes its colour from its name, whatever index
    /// is stored with it. Finder on macOS 27 writes every tag with index 1
    /// (grey) — `"Green\n1"` — and keeps the real colour in its iCloud tag
    /// store, which other apps can't read; Finder itself resolves the colour
    /// by the tag's name. Read by index, a tag given in Finder showed as a
    /// grey dot. Any other tag keeps the index stored with it.
    public static func decode(_ raw: String) -> FileTag? {
        let parts = raw.split(separator: "\n", maxSplits: 1, omittingEmptySubsequences: false)
        guard let first = parts.first, !first.isEmpty else { return nil }
        let name = String(first)
        if let standard = standardColor(named: name) {
            return FileTag(name: name, color: standard)
        }
        var color = TagColor.none
        if parts.count == 2, let index = Int(parts[1]), let known = TagColor(rawValue: index) {
            color = known
        }
        return FileTag(name: name, color: color)
    }

    // MARK: - Standard colour tags

    /// The colour of a standard colour tag, by its name, or nil for any other
    /// tag. Knows the names this Mac gives the seven tags (see
    /// `registerSystemNames`, which follows the system language and the
    /// user's renames) and the standard names in the common languages, since
    /// a file can carry tags given on a Mac set to another language.
    public static func standardColor(named name: String) -> TagColor? {
        let key = normalized(name)
        if let color = systemNames.withLock({ $0[key] }) {
            return color
        }
        return localizedStandardNames[key]
    }

    /// Records the names this Mac gives the seven colour tags (from
    /// `NSWorkspace.fileLabels`), so a renamed standard tag still gets its
    /// colour. Called by the app at launch.
    public static func registerSystemNames(_ tags: [FileTag]) {
        let table = Dictionary(
            tags.filter { $0.color != .none && !$0.name.isEmpty }.map { (normalized($0.name), $0.color) },
            uniquingKeysWith: { first, _ in first }
        )
        systemNames.withLock { $0 = table }
    }

    /// Case and Unicode form don't matter: a name can arrive decomposed
    /// ("Gru\u{308}n") from another file system.
    private static func normalized(_ name: String) -> String {
        name.precomposedStringWithCanonicalMapping.lowercased()
    }

    private static let systemNames = TagNameTable()

    /// Finder's standard names for the seven colour tags in English, German,
    /// French, Spanish, Italian, Dutch, Japanese and Chinese.
    private static let localizedStandardNames: [String: TagColor] = {
        let names: [TagColor: [String]] = [
            .red: ["Red", "Rot", "Rouge", "Rojo", "Rosso", "Rood", "レッド", "红色", "紅色"],
            .orange: ["Orange", "Naranja", "Arancione", "Oranje", "オレンジ", "橙色"],
            .yellow: ["Yellow", "Gelb", "Jaune", "Amarillo", "Giallo", "Geel", "イエロー", "黄色", "黃色"],
            .green: ["Green", "Grün", "Vert", "Verde", "Groen", "グリーン", "绿色", "綠色"],
            .blue: ["Blue", "Blau", "Bleu", "Azul", "Blu", "Blauw", "ブルー", "蓝色", "藍色"],
            .purple: ["Purple", "Lila", "Violet", "Morado", "Viola", "Paars", "パープル", "紫色"],
            .gray: ["Gray", "Grey", "Grau", "Gris", "Grigio", "Grijs", "グレイ", "灰色"],
        ]
        var table: [String: TagColor] = [:]
        for (color, list) in names {
            for name in list { table[normalized(name)] = color }
        }
        return table
    }()

    public static func encode(_ tag: FileTag) -> String {
        "\(tag.name)\n\(tag.color.rawValue)"
    }

    static func decode(attribute data: Data) -> [FileTag] {
        guard let list = (try? PropertyListSerialization.propertyList(from: data, format: nil)) as? [String] else {
            return []
        }
        return list.compactMap(decode)
    }

    static func encode(attribute tags: [FileTag]) throws -> Data {
        try PropertyListSerialization.data(fromPropertyList: tags.map(encode), format: .binary, options: 0)
    }

    // MARK: - Reading

    /// The tags on one file or folder. Empty when it has none or can't be read.
    public static func read(at url: URL) -> [FileTag] {
        read(atPath: url.path)
    }

    static func read(atPath path: String) -> [FileTag] {
        // Almost every file has no tags, and the attribute of one that has
        // is small: one call with a stack buffer settles both cases.
        var stack = [UInt8](repeating: 0, count: 512)
        let length = getxattr(path, attributeName, &stack, stack.count, 0, 0)
        if length > 0 {
            return decode(attribute: Data(stack[0..<length]))
        }
        guard length < 0, errno == ERANGE else { return [] }
        let needed = getxattr(path, attributeName, nil, 0, 0, 0)
        guard needed > 0 else { return [] }
        var data = Data(count: needed)
        let got = data.withUnsafeMutableBytes { getxattr(path, attributeName, $0.baseAddress, needed, 0, 0) }
        guard got > 0 else { return [] }
        return decode(attribute: data.prefix(got))
    }

    /// The tags of many files, keyed by the caller's own key for each.
    /// Files without tags are left out. Runs off the caller's thread.
    public static func readAll(_ files: [(key: String, path: String)]) async -> [String: [FileTag]] {
        guard !files.isEmpty else { return [:] }
        return await withCheckedContinuation { continuation in
            queue.async {
                var result: [String: [FileTag]] = [:]
                for file in files {
                    let tags = read(atPath: file.path)
                    if !tags.isEmpty { result[file.key] = tags }
                }
                continuation.resume(returning: result)
            }
        }
    }

    // MARK: - Writing

    public struct WriteError: LocalizedError {
        public let path: String
        public let code: Int32

        public var errorDescription: String? {
            String(cString: strerror(code))
        }
    }

    /// Replaces the tags on one file or folder; an empty list clears them.
    public static func write(_ tags: [FileTag], to url: URL) throws {
        let path = url.path
        if tags.isEmpty {
            if removexattr(path, attributeName, 0) != 0, errno != ENOATTR {
                throw WriteError(path: path, code: errno)
            }
        } else {
            let data = try encode(attribute: tags)
            let status = data.withUnsafeBytes { setxattr(path, attributeName, $0.baseAddress, data.count, 0, 0) }
            if status != 0 {
                throw WriteError(path: path, code: errno)
            }
        }
        // The old Finder label lives apart from the tags, and the system
        // makes a tag up from it when the attribute is gone: a file whose
        // attribute was cleared but whose label still said "red" would go
        // on showing as red. So the label always follows the tags.
        let label = tags.folderTint?.rawValue ?? 0
        try? (url as NSURL).setResourceValue(label, forKey: .labelNumberKey)
    }

    /// Applies a change to what the file has now, so a tag given in Finder
    /// a moment ago isn't written over. Returns the tags afterwards.
    @discardableResult
    public static func apply(_ change: TagChange, to url: URL) throws -> [FileTag] {
        let current = read(at: url)
        let updated = change.applied(to: current)
        if updated != current {
            try write(updated, to: url)
        }
        return updated
    }

    /// Applies one change to several files off the caller's thread. Returns
    /// each file's tags afterwards and the first failure, if there was one.
    public static func applyAll(
        _ change: TagChange,
        to files: [(key: String, url: URL)]
    ) async -> (tags: [String: [FileTag]], failure: (name: String, message: String)?) {
        await withCheckedContinuation { continuation in
            queue.async {
                var result: [String: [FileTag]] = [:]
                var failure: (name: String, message: String)?
                for file in files {
                    do {
                        result[file.key] = try apply(change, to: file.url)
                    } catch {
                        result[file.key] = read(at: file.url)
                        if failure == nil {
                            failure = (file.url.lastPathComponent, error.localizedDescription)
                        }
                    }
                }
                continuation.resume(returning: (result, failure))
            }
        }
    }

    private static let queue = DispatchQueue(
        label: "com.rana.FileFluss.finderTags",
        qos: .userInitiated,
        attributes: .concurrent
    )
}

/// The system's names for the colour tags: set at launch on the main thread,
/// read on the tag queue while folders are listed.
final class TagNameTable: @unchecked Sendable {
    private let lock = NSLock()
    private var table: [String: TagColor] = [:]

    func withLock<T>(_ body: (inout [String: TagColor]) -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body(&table)
    }
}
