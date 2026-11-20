import Foundation

extension CloudProviderType {
    /// Whether the identifier this provider puts in `CloudFileItem.id` is
    /// the service's own and survives a rename or move. For the others the
    /// identifier is the path, or made from it, and says nothing a path
    /// doesn't.
    public var hasLastingItemIDs: Bool {
        switch self {
        case .pCloud, .kDrive, .oneDrive, .googleDrive, .googleDrivePicker,
             .dropbox, .mega, .filen, .internxt, .terabox:
            return true
        default:
            return false
        }
    }

    /// iCloud Drive is a folder on this Mac: its items are real files and
    /// keep their Finder tags themselves, which iCloud then syncs.
    public var keepsTagsInFiles: Bool {
        self == .iCloud
    }
}

/// Takes tags across a transfer between this Mac and a cloud account.
///
/// A local file has its tags in the file; a cloud item has them in the
/// `TagStore`. So an upload copies the file's tags into the store and a
/// download writes the store's tags onto the file. A transfer from one
/// account to another passes through a local copy, and gets its tags across
/// by doing both.
///
/// A transfer only ever adds tags. An untagged file uploaded over a tagged
/// cloud item leaves that item's tags alone: a sync that refreshes a file's
/// contents must not quietly take off the tag someone gave it on the other
/// side.
public enum TagTransfer {
    /// Call after `localURL` — a file, or a folder with everything in it —
    /// has been uploaded to `remotePath`.
    public static func uploaded(
        from localURL: URL,
        toAccount account: UUID,
        providerType: CloudProviderType?,
        remotePath: String,
        store: TagStore = .shared
    ) async {
        // The upload was a file copy, which took the tags with it.
        if providerType?.keepsTagsInFiles == true { return }
        let found = await taggedItems(under: localURL, remotePath: remotePath)
        guard !found.isEmpty else { return }
        await store.setTags(found, account: account)
    }

    /// The tagged items at and below `localURL`, each with the remote path
    /// it was uploaded to. Walks the folder off the caller's thread.
    private static func taggedItems(
        under localURL: URL,
        remotePath: String
    ) async -> [(path: String, tags: [FileTag])] {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                var found: [(path: String, tags: [FileTag])] = []
                let rootTags = FinderTags.read(at: localURL)
                if !rootTags.isEmpty { found.append((remotePath, rootTags)) }

                var isDirectory: ObjCBool = false
                let fm = FileManager.default
                if fm.fileExists(atPath: localURL.path, isDirectory: &isDirectory), isDirectory.boolValue,
                   let walker = fm.enumerator(atPath: localURL.path) {
                    let base = remotePath == "/" ? "" : remotePath
                    while let relative = walker.nextObject() as? String {
                        let tags = FinderTags.read(atPath: localURL.path + "/" + relative)
                        if !tags.isEmpty { found.append((base + "/" + relative, tags)) }
                    }
                }
                continuation.resume(returning: found)
            }
        }
    }

    /// Call after `remotePath` has been downloaded to `localURL`. `known`
    /// is for an item whose tags the caller has just learned from the
    /// service itself — one inside a folder FileFluss has never listed in a
    /// panel, which the record therefore says nothing about.
    public static func downloaded(
        fromAccount account: UUID,
        providerType: CloudProviderType?,
        remotePath: String,
        to localURL: URL,
        known: [FileTag]? = nil,
        store: TagStore = .shared
    ) async {
        if providerType?.keepsTagsInFiles == true { return }
        let tags: [FileTag]
        if let known {
            tags = known
        } else {
            tags = await store.tags(account: account, path: remotePath)
        }
        guard !tags.isEmpty else { return }
        try? FinderTags.write(tags, to: localURL)
    }
}
