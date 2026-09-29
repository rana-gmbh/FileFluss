import Foundation

public extension Dictionary where Key == String {

    /// Removes `path` and everything cached beneath it.
    ///
    /// Providers that address items by a server-assigned id keep a path → id
    /// cache so navigating doesn't re-walk the tree on every step. Deleting a
    /// folder takes its whole subtree with it, and a rename or move takes
    /// every descendant's path with it — so evicting only the exact key
    /// leaves the children mapped to ids that name nothing.
    ///
    /// That is not a stale-read problem, it is a write problem: `createFolder`
    /// is idempotent by asking the cache first, so a folder recreated under a
    /// name that was deleted earlier in the session is reported as already
    /// there and never created, and every upload into it is then rejected for
    /// a parent that doesn't exist. The version test caught it by running
    /// twice against one launch of the app: the second run failed every
    /// upload into a subfolder the first run had cleaned up.
    ///
    /// Boundaries are on path components, so evicting `/a/b` leaves `/a/bc`
    /// alone. The root key is kept: its id is a constant the client seeds
    /// itself with and the walk starts from it.
    mutating func removePathSubtree(_ path: String) {
        let trimmed = Self.trimmedPath(path)
        if trimmed != "/" { removeValue(forKey: trimmed) }
        let prefix = trimmed == "/" ? "/" : trimmed + "/"
        for key in keys where key != "/" && key.hasPrefix(prefix) {
            removeValue(forKey: key)
        }
    }

    /// Makes a completed listing authoritative for its own level: anything
    /// cached directly under `path` that the listing didn't contain is
    /// dropped, along with its subtree.
    ///
    /// These caches are filled from listings but were only ever added to, so
    /// an entry that disappeared — deleted from another device, or deleted
    /// here while the provider's listing still briefly showed it — stayed
    /// cached for the life of the session. `createFolder` asks the cache
    /// first and treats a hit as "already there", so the next folder of that
    /// name was never created and everything written into it failed. Seen on
    /// Internxt in the version test: run one cleaned up, run two created
    /// nothing and reported the folder missing from the listing.
    ///
    /// Only ever call this after a listing has completed. A listing that
    /// threw part-way through is not evidence that anything is gone.
    mutating func retainPathChildren(of path: String, named names: Set<String>) {
        let parent = Self.trimmedPath(path)
        let prefix = parent == "/" ? "/" : parent + "/"
        let stale = keys.filter { key in
            guard key != "/", key.hasPrefix(prefix) else { return false }
            let remainder = key.dropFirst(prefix.count)
            // Direct children only; the grandchildren go with their parent.
            guard !remainder.isEmpty, !remainder.contains("/") else { return false }
            return !names.contains(String(remainder))
        }
        for key in stale { removePathSubtree(key) }
    }

    private static func trimmedPath(_ path: String) -> String {
        path.count > 1 && path.hasSuffix("/") ? String(path.dropLast()) : path
    }
}
