import Foundation

/// Decoded form of the GoPro camera's `GET /gopro/media/list` response.
///
/// Shape (Open GoPro HTTP API):
/// ```json
/// {
///   "id": "...",
///   "media": [
///     { "d": "100GOPRO",
///       "fs": [ { "n": "GH010397.MP4", "cre": "1696600109", "mod": "1696600109", "s": "11587660" } ] }
///   ]
/// }
/// ```
/// The camera groups files by their on-card directory (`d`, e.g. `100GOPRO`);
/// each `fs` entry is one media file. Numeric fields arrive as *strings*, which
/// is why `size`/dates are decoded leniently below.
struct GoProMediaList: Decodable {
    let media: [Directory]

    struct Directory: Decodable {
        /// On-card directory name, e.g. `100GOPRO`.
        let directory: String
        let files: [File]

        enum CodingKeys: String, CodingKey {
            case directory = "d"
            case files = "fs"
        }
    }

    struct File: Decodable {
        /// File name, e.g. `GH010397.MP4`.
        let name: String
        /// Size in bytes (the API sends it as a string).
        let size: Int64
        /// Last-modified, Unix seconds (string in the API; may be absent).
        let modified: Date?
        /// Created, Unix seconds (string in the API; may be absent).
        let created: Date?

        enum CodingKeys: String, CodingKey {
            case name = "n"
            case size = "s"
            case modified = "mod"
            case created = "cre"
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            name = try c.decode(String.self, forKey: .name)
            size = Self.int64(from: try? c.decode(GoProScalar.self, forKey: .size)) ?? 0
            modified = Self.date(from: try? c.decode(GoProScalar.self, forKey: .modified))
            created = Self.date(from: try? c.decode(GoProScalar.self, forKey: .created))
        }

        private static func int64(from scalar: GoProScalar?) -> Int64? {
            switch scalar {
            case .string(let s): return Int64(s)
            case .number(let n): return Int64(n)
            case .none: return nil
            }
        }

        private static func date(from scalar: GoProScalar?) -> Date? {
            guard let seconds = int64(from: scalar), seconds > 0 else { return nil }
            return Date(timeIntervalSince1970: TimeInterval(seconds))
        }
    }
}

/// A JSON value that GoPro firmware inconsistently encodes as either a
/// quoted string or a bare number depending on model/version. Decoding
/// through this keeps the file models tolerant of both.
private enum GoProScalar: Decodable {
    case string(String)
    case number(Double)

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if let s = try? c.decode(String.self) {
            self = .string(s)
        } else if let n = try? c.decode(Double.self) {
            self = .number(n)
        } else {
            self = .number(0)
        }
    }
}

extension GoProMediaList {
    /// Builds the listing for a given path in FileFluss's path convention:
    /// - `"/"` → the on-card directories (`100GOPRO`, …) as folders
    /// - `"/100GOPRO"` → that directory's media files
    ///
    /// Paths are always `"/<dir>"` for folders and `"/<dir>/<file>"` for files
    /// so they round-trip through download/delete (which strip the leading `/`).
    func items(at path: String) -> [CloudFileItem] {
        let trimmed = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))

        if trimmed.isEmpty {
            // Root: one folder per on-card directory.
            var folders: [CloudFileItem] = []
            for dir in media {
                var total: Int64 = 0
                var newest: Date = .distantPast
                for file in dir.files {
                    total += file.size
                    if let m = file.modified, m > newest { newest = m }
                }
                let dirPath = "/" + dir.directory
                folders.append(CloudFileItem(
                    id: dirPath,
                    name: dir.directory,
                    path: dirPath,
                    isDirectory: true,
                    size: total,
                    modificationDate: newest,
                    checksum: nil
                ))
            }
            return folders
        }

        // Inside a directory: its files.
        guard let dir = media.first(where: { $0.directory == trimmed }) else { return [] }
        var files: [CloudFileItem] = []
        for file in dir.files {
            files.append(makeItem(directory: dir.directory, file: file))
        }
        return files
    }

    private func makeItem(directory: String, file: File) -> CloudFileItem {
        let p = "/\(directory)/\(file.name)"
        let date: Date = file.modified ?? file.created ?? .distantPast
        return CloudFileItem(
            id: p,
            name: file.name,
            path: p,
            isDirectory: false,
            size: file.size,
            modificationDate: date,
            checksum: nil
        )
    }

    /// Looks up a single file's metadata by its `"/<dir>/<file>"` path.
    func file(at path: String) -> CloudFileItem? {
        let comps = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard comps.count == 2,
              let dir = media.first(where: { $0.directory == comps[0] }),
              let file = dir.files.first(where: { $0.name == comps[1] }) else { return nil }
        return makeItem(directory: dir.directory, file: file)
    }
}
