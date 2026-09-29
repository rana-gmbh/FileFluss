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
        let trimmed = path.count > 1 && path.hasSuffix("/") ? String(path.dropLast()) : path
        if trimmed != "/" { removeValue(forKey: trimmed) }
        let prefix = trimmed == "/" ? "/" : trimmed + "/"
        for key in keys where key != "/" && key.hasPrefix(prefix) {
            removeValue(forKey: key)
        }
    }
}
