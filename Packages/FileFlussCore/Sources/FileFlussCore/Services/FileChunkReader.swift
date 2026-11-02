import Foundation

/// Reads a local file one piece at a time, for chunked uploads.
///
/// Loading the whole file to slice it meant a 20 GB upload needed 20 GB of
/// memory before a byte went out. A file that shrank mid-upload also crashed
/// on the out-of-range slice, where this reports it.
public final class FileChunkReader {
    private let handle: FileHandle

    public init(url: URL) throws {
        handle = try FileHandle(forReadingFrom: url)
    }

    deinit {
        try? handle.close()
    }

    public func read(offset: Int, length: Int) throws -> Data {
        try handle.seek(toOffset: UInt64(offset))
        guard let data = try handle.read(upToCount: length), data.count == length else {
            throw CloudProviderError.commandFailed(L10n.text("The file changed while it was being uploaded."))
        }
        return data
    }

    /// A file's size, without reading it.
    public static func size(of url: URL) throws -> Int {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes[.size] as? NSNumber)?.intValue ?? 0
    }
}
