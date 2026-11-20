import Foundation

/// The tags FileFluss keeps for items on cloud accounts.
///
/// Local files carry their tags themselves (see `FinderTags`). Most cloud
/// services have nothing to carry them in, and the few that have each do it
/// differently, so for every account the tags live here: one small JSON file
/// per account, read once and held in memory.
///
/// An entry belongs to a path. Where the service gives items an identifier
/// that outlives a rename, the entry remembers that too, and `resolve`
/// uses it to follow an item that was renamed or moved somewhere other than
/// in FileFluss. Without such an identifier the path is all there is, and a
/// rename made elsewhere leaves the tag behind.
///
/// FileFluss's own renames, moves, copies and deletes are passed on by the
/// callers through `moveSubtree`, `copySubtree` and `removeSubtree`.
public actor TagStore {
    public static let shared = TagStore()

    struct Entry: Codable, Equatable {
        var tags: [FileTag]
        var itemID: String?
    }

    private struct AccountFile: Codable {
        var version = 1
        var entries: [String: Entry]
    }

    private let directory: URL
    private var accounts: [UUID: [String: Entry]] = [:]
    private var dirty: Set<UUID> = []
    private var saveTask: Task<Void, Never>?

    private init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        self.directory = appSupport
            .appendingPathComponent("FileFluss", isDirectory: true)
            .appendingPathComponent("Tags", isDirectory: true)
    }

    /// A store in a folder of the caller's choosing, for tests.
    public init(directory: URL) {
        self.directory = directory
    }

    // MARK: - Reading

    public func tags(account: UUID, path: String) -> [FileTag] {
        entries(for: account)[Self.normalized(path)]?.tags ?? []
    }

    /// The tags of the items in one folder listing, keyed by path. Items
    /// without tags are left out.
    ///
    /// With `stableIDs`, an entry is matched by the item's identifier first,
    /// and follows the item to its new path if it was renamed elsewhere. An
    /// item that has no entry of its own then takes the one at its path —
    /// that is a file replaced in place, which gets a new identifier but is
    /// to the user the same file.
    public func resolve(
        account: UUID,
        items: [(id: String, path: String)],
        stableIDs: Bool
    ) -> [String: [FileTag]] {
        var entries = entries(for: account)
        guard !entries.isEmpty else { return [:] }
        var result: [String: [FileTag]] = [:]

        guard stableIDs else {
            for item in items {
                if let entry = entries[Self.normalized(item.path)] {
                    result[item.path] = entry.tags
                }
            }
            return result
        }

        var pathByID: [String: String] = [:]
        for (path, entry) in entries {
            if let id = entry.itemID { pathByID[id] = path }
        }

        var changed = false
        // Matched entries are taken out first and put back at the end, so
        // two items that swapped names don't overwrite one another.
        var matched: [(path: String, entry: Entry)] = []
        var unmatched: [(id: String, path: String)] = []
        for item in items {
            let path = Self.normalized(item.path)
            if let oldPath = pathByID[item.id], let entry = entries.removeValue(forKey: oldPath) {
                if oldPath != path { changed = true }
                matched.append((path, entry))
                result[item.path] = entry.tags
            } else {
                unmatched.append(item)
            }
        }
        for item in unmatched {
            let path = Self.normalized(item.path)
            guard var entry = entries.removeValue(forKey: path) else { continue }
            entry.itemID = item.id
            changed = true
            matched.append((path, entry))
            result[item.path] = entry.tags
        }
        for (path, entry) in matched {
            entries[path] = entry
        }

        if changed {
            accounts[account] = entries
            markDirty(account)
        }
        return result
    }

    // MARK: - Writing

    /// Sets an item's tags; an empty list forgets the item. `itemID` is the
    /// service's identifier where it has a lasting one, and nil for an item
    /// that has only just been written, whose identifier isn't known yet.
    public func setTags(_ tags: [FileTag], account: UUID, path: String, itemID: String? = nil) {
        var entries = entries(for: account)
        let path = Self.normalized(path)
        let updated = tags.isEmpty ? nil : Entry(tags: tags, itemID: itemID)
        guard entries[path] != updated else { return }
        entries[path] = updated
        accounts[account] = entries
        markDirty(account)
    }

    /// Sets the tags of several items that have just been written.
    public func setTags(_ batch: [(path: String, tags: [FileTag])], account: UUID) {
        for item in batch {
            setTags(item.tags, account: account, path: item.path)
        }
    }

    /// A rename or move: the item's tags, and those of everything inside
    /// it, go with it. Whatever was at the destination is replaced.
    public func moveSubtree(account: UUID, from: String, to: String) {
        transfer(fromAccount: account, path: from, toAccount: account, to: to, keepSource: false)
    }

    /// A copy, within an account or from one to another. The copies are new
    /// items, so they start without an identifier.
    public func copySubtree(fromAccount: UUID, path: String, toAccount: UUID, to: String) {
        transfer(fromAccount: fromAccount, path: path, toAccount: toAccount, to: to, keepSource: true)
    }

    public func removeSubtree(account: UUID, path: String) {
        var entries = entries(for: account)
        let doomed = Self.subtreeKeys(in: entries, under: Self.normalized(path))
        guard !doomed.isEmpty else { return }
        for key in doomed { entries[key] = nil }
        accounts[account] = entries
        markDirty(account)
    }

    /// Forgets an account that has been removed from FileFluss.
    public func removeAccount(_ account: UUID) {
        accounts[account] = [:]
        markDirty(account)
    }

    private func transfer(fromAccount: UUID, path: String, toAccount: UUID, to: String, keepSource: Bool) {
        let from = Self.normalized(path)
        let to = Self.normalized(to)
        guard !(fromAccount == toAccount && from == to) else { return }

        var source = entries(for: fromAccount)
        let keys = Self.subtreeKeys(in: source, under: from)
        var moving: [(String, Entry)] = []
        for key in keys {
            guard var entry = source[key] else { continue }
            if keepSource {
                entry.itemID = nil
            } else {
                source[key] = nil
            }
            moving.append((to + String(key.dropFirst(from.count)), entry))
        }

        var destination = fromAccount == toAccount ? source : entries(for: toAccount)
        let replaced = Self.subtreeKeys(in: destination, under: to)
        guard !moving.isEmpty || !replaced.isEmpty else { return }
        for key in replaced { destination[key] = nil }
        for (key, entry) in moving { destination[key] = entry }

        if fromAccount != toAccount, !keepSource, !keys.isEmpty {
            accounts[fromAccount] = source
            markDirty(fromAccount)
        }
        accounts[toAccount] = destination
        markDirty(toAccount)
    }

    // MARK: - Storage

    private static func normalized(_ path: String) -> String {
        guard path.count > 1, path.hasSuffix("/") else { return path }
        return String(path.dropLast())
    }

    /// The path itself and everything below it.
    private static func subtreeKeys(in entries: [String: Entry], under path: String) -> [String] {
        let prefix = path == "/" ? "/" : path + "/"
        return entries.keys.filter { $0 == path || $0.hasPrefix(prefix) }
    }

    private func fileURL(for account: UUID) -> URL {
        directory.appendingPathComponent("\(account.uuidString).json")
    }

    private func entries(for account: UUID) -> [String: Entry] {
        if let loaded = accounts[account] { return loaded }
        var entries: [String: Entry] = [:]
        if let data = try? Data(contentsOf: fileURL(for: account)),
           let file = try? JSONDecoder().decode(AccountFile.self, from: data) {
            entries = file.entries
        }
        accounts[account] = entries
        return entries
    }

    /// Writes are gathered for a moment: an upload of a few thousand tagged
    /// files would otherwise rewrite the file once per file.
    private func markDirty(_ account: UUID) {
        dirty.insert(account)
        guard saveTask == nil else { return }
        saveTask = Task {
            try? await Task.sleep(for: .milliseconds(400))
            self.flush()
        }
    }

    /// Writes pending changes now.
    public func flush() {
        saveTask?.cancel()
        saveTask = nil
        let pending = dirty
        dirty.removeAll()
        guard !pending.isEmpty else { return }
        let fm = FileManager.default
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        for account in pending {
            let url = fileURL(for: account)
            let entries = accounts[account] ?? [:]
            if entries.isEmpty {
                try? fm.removeItem(at: url)
            } else if let data = try? encoder.encode(AccountFile(entries: entries)) {
                try? data.write(to: url, options: .atomic)
            }
        }
    }
}
