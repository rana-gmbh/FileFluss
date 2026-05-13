import Foundation
import SQLite3

actor SearchIndex {
    static let shared = SearchIndex()

    private var db: OpaquePointer?
    private let dbPath: String

    private init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = appSupport.appendingPathComponent("FileFluss", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        self.dbPath = dir.appendingPathComponent("search_index.db").path
    }

    func open() throws {
        guard db == nil else { return }
        guard sqlite3_open(dbPath, &db) == SQLITE_OK else {
            throw SearchIndexError.openFailed(String(cString: sqlite3_errmsg(db)))
        }

        try execute("""
            CREATE TABLE IF NOT EXISTS cloud_files (
                account_id TEXT NOT NULL,
                path TEXT NOT NULL,
                name TEXT NOT NULL,
                is_directory INTEGER NOT NULL,
                size INTEGER NOT NULL,
                modification_date REAL NOT NULL,
                checksum TEXT,
                last_indexed REAL NOT NULL,
                PRIMARY KEY (account_id, path)
            )
        """)

        try execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS cloud_files_fts USING fts5(
                name,
                content=cloud_files,
                content_rowid=rowid
            )
        """)

        // Triggers to keep FTS in sync
        try execute("""
            CREATE TRIGGER IF NOT EXISTS cloud_files_ai AFTER INSERT ON cloud_files BEGIN
                INSERT INTO cloud_files_fts(rowid, name) VALUES (new.rowid, new.name);
            END
        """)
        try execute("""
            CREATE TRIGGER IF NOT EXISTS cloud_files_ad AFTER DELETE ON cloud_files BEGIN
                INSERT INTO cloud_files_fts(cloud_files_fts, rowid, name) VALUES('delete', old.rowid, old.name);
            END
        """)
        try execute("""
            CREATE TRIGGER IF NOT EXISTS cloud_files_au AFTER UPDATE ON cloud_files BEGIN
                INSERT INTO cloud_files_fts(cloud_files_fts, rowid, name) VALUES('delete', old.rowid, old.name);
                INSERT INTO cloud_files_fts(rowid, name) VALUES (new.rowid, new.name);
            END
        """)

        // --- Unified indexed files table for drives (and any future
        // index-anything source). Cloud accounts continue to use
        // cloud_files above. The unified table is keyed by an opaque
        // source_id string so it can hold drives, network mounts, etc.

        try execute("""
            CREATE TABLE IF NOT EXISTS indexed_sources (
                source_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                display_name TEXT NOT NULL,
                last_indexed REAL,
                total_files INTEGER DEFAULT 0,
                total_bytes INTEGER DEFAULT 0
            )
        """)

        try execute("""
            CREATE TABLE IF NOT EXISTS indexed_files (
                source_id TEXT NOT NULL,
                path TEXT NOT NULL,
                parent_path TEXT NOT NULL,
                name TEXT NOT NULL,
                is_directory INTEGER NOT NULL,
                size INTEGER NOT NULL,
                modification_date REAL NOT NULL,
                PRIMARY KEY (source_id, path)
            )
        """)

        try execute("""
            CREATE INDEX IF NOT EXISTS idx_indexed_files_parent
            ON indexed_files(source_id, parent_path)
        """)

        try execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS indexed_files_fts USING fts5(
                source_id UNINDEXED,
                name,
                path UNINDEXED,
                tokenize='unicode61'
            )
        """)

        // One-shot migration: earlier builds wrote garbage (5 zero bytes
        // from a dangling pointer — see the comment in `recordCloudSource`)
        // into `indexed_sources.kind` for every cloud account. The exact
        // stored value isn't empty, NULL, or matchable as ASCII text, so
        // detect those rows by exclusion: any source that isn't a drive
        // (drives use the `vol:` prefix on `source_id`) and isn't already
        // tagged as `cloud` must be a cloud account that needs the fix.
        try? execute("""
            UPDATE indexed_sources
            SET kind = 'cloud'
            WHERE source_id NOT LIKE 'vol:%'
              AND kind != 'cloud'
        """)
    }

    func close() {
        if let db {
            sqlite3_close(db)
        }
        db = nil
    }

    func upsertItems(_ items: [CloudFileItem], accountId: UUID) {
        guard let db else { return }
        let accountStr = accountId.uuidString
        let now = Date().timeIntervalSince1970

        let sql = """
            INSERT OR REPLACE INTO cloud_files
            (account_id, path, name, is_directory, size, modification_date, checksum, last_indexed)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        """

        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
        defer { sqlite3_finalize(stmt) }

        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        for item in items {
            sqlite3_bind_text(stmt, 1, (accountStr as NSString).utf8String, -1, nil)
            sqlite3_bind_text(stmt, 2, (item.path as NSString).utf8String, -1, nil)
            sqlite3_bind_text(stmt, 3, (item.name as NSString).utf8String, -1, nil)
            sqlite3_bind_int(stmt, 4, item.isDirectory ? 1 : 0)
            sqlite3_bind_int64(stmt, 5, item.size)
            sqlite3_bind_double(stmt, 6, item.modificationDate.timeIntervalSince1970)
            if let checksum = item.checksum {
                sqlite3_bind_text(stmt, 7, (checksum as NSString).utf8String, -1, nil)
            } else {
                sqlite3_bind_null(stmt, 7)
            }
            sqlite3_bind_double(stmt, 8, now)
            sqlite3_step(stmt)
            sqlite3_reset(stmt)
        }
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    func removeItems(accountId: UUID, paths: [String]) {
        guard let db else { return }
        let accountStr = accountId.uuidString
        for path in paths {
            let sql = "DELETE FROM cloud_files WHERE account_id = ? AND path = ?"
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { continue }
            sqlite3_bind_text(stmt, 1, (accountStr as NSString).utf8String, -1, nil)
            sqlite3_bind_text(stmt, 2, (path as NSString).utf8String, -1, nil)
            sqlite3_step(stmt)
            sqlite3_finalize(stmt)
        }
    }

    func removeAllItems(accountId: UUID) {
        guard let db else { return }
        let sql = "DELETE FROM cloud_files WHERE account_id = ?"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, nil)
        sqlite3_step(stmt)
        sqlite3_finalize(stmt)
    }

    func search(query: String, accountId: UUID?, limit: Int = 500) -> [IndexedCloudFile] {
        guard let db else { return [] }

        let ftsQuery = Self.makeFTSQuery(from: query)
        guard !ftsQuery.isEmpty else { return [] }

        let sql: String
        if let accountId {
            sql = """
                SELECT cf.account_id, cf.path, cf.name, cf.is_directory, cf.size, cf.modification_date, cf.checksum, cf.last_indexed
                FROM cloud_files cf
                JOIN cloud_files_fts fts ON cf.rowid = fts.rowid
                WHERE fts.name MATCH ? AND cf.account_id = ?
                LIMIT ?
            """
        } else {
            sql = """
                SELECT cf.account_id, cf.path, cf.name, cf.is_directory, cf.size, cf.modification_date, cf.checksum, cf.last_indexed
                FROM cloud_files cf
                JOIN cloud_files_fts fts ON cf.rowid = fts.rowid
                WHERE fts.name MATCH ?
                LIMIT ?
            """
        }

        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }

        sqlite3_bind_text(stmt, 1, (ftsQuery as NSString).utf8String, -1, nil)
        if let accountId {
            sqlite3_bind_text(stmt, 2, (accountId.uuidString as NSString).utf8String, -1, nil)
            sqlite3_bind_int(stmt, 3, Int32(limit))
        } else {
            sqlite3_bind_int(stmt, 2, Int32(limit))
        }

        var results: [IndexedCloudFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            let accId = String(cString: sqlite3_column_text(stmt, 0))
            let path = String(cString: sqlite3_column_text(stmt, 1))
            let name = String(cString: sqlite3_column_text(stmt, 2))
            let isDir = sqlite3_column_int(stmt, 3) == 1
            let size = sqlite3_column_int64(stmt, 4)
            let modDate = Date(timeIntervalSince1970: sqlite3_column_double(stmt, 5))
            let checksum: String? = sqlite3_column_type(stmt, 6) != SQLITE_NULL
                ? String(cString: sqlite3_column_text(stmt, 6)) : nil
            let lastIndexed = Date(timeIntervalSince1970: sqlite3_column_double(stmt, 7))

            let item = CloudFileItem(
                id: path,
                name: name,
                path: path,
                isDirectory: isDir,
                size: size,
                modificationDate: modDate,
                checksum: checksum
            )
            if let uuid = UUID(uuidString: accId) {
                results.append(IndexedCloudFile(accountId: uuid, item: item, lastIndexed: lastIndexed))
            }
        }
        return results
    }

    // MARK: - Unified indexed_files / indexed_sources API

    struct IndexedFile: Sendable, Hashable, Identifiable {
        let sourceId: String
        let path: String
        let parentPath: String
        let name: String
        let isDirectory: Bool
        let size: Int64
        let modificationDate: Date

        var id: String { "\(sourceId)|\(path)" }
    }

    struct IndexedSource: Sendable, Hashable {
        let sourceId: String
        let kind: String
        let displayName: String
        let lastIndexed: Date?
        let totalFiles: Int
        let totalBytes: Int64
    }

    /// Replace this source's stored file rows with `files`. Caller is
    /// responsible for providing a fully-walked snapshot; partial updates
    /// would corrupt the index. Runs in a single transaction.
    func replaceFiles(sourceId: String, kind: String, displayName: String, files: [IndexedFile]) {
        guard let db else { return }
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)

        // Wipe prior rows for this source.
        var del: OpaquePointer?
        sqlite3_prepare_v2(db, "DELETE FROM indexed_files WHERE source_id = ?", -1, &del, nil)
        sqlite3_bind_text(del, 1, (sourceId as NSString).utf8String, -1, nil)
        sqlite3_step(del)
        sqlite3_finalize(del)

        var delFts: OpaquePointer?
        sqlite3_prepare_v2(db, "DELETE FROM indexed_files_fts WHERE source_id = ?", -1, &delFts, nil)
        sqlite3_bind_text(delFts, 1, (sourceId as NSString).utf8String, -1, nil)
        sqlite3_step(delFts)
        sqlite3_finalize(delFts)

        // Insert rows.
        let insSQL = """
            INSERT INTO indexed_files (source_id, path, parent_path, name, is_directory, size, modification_date)
            VALUES (?, ?, ?, ?, ?, ?, ?)
        """
        var ins: OpaquePointer?
        sqlite3_prepare_v2(db, insSQL, -1, &ins, nil)

        let ftsSQL = "INSERT INTO indexed_files_fts (source_id, name, path) VALUES (?, ?, ?)"
        var fts: OpaquePointer?
        sqlite3_prepare_v2(db, ftsSQL, -1, &fts, nil)

        var totalFiles = 0
        var totalBytes: Int64 = 0

        for f in files {
            sqlite3_bind_text(ins, 1, (f.sourceId as NSString).utf8String, -1, nil)
            sqlite3_bind_text(ins, 2, (f.path as NSString).utf8String, -1, nil)
            sqlite3_bind_text(ins, 3, (f.parentPath as NSString).utf8String, -1, nil)
            sqlite3_bind_text(ins, 4, (f.name as NSString).utf8String, -1, nil)
            sqlite3_bind_int(ins, 5, f.isDirectory ? 1 : 0)
            sqlite3_bind_int64(ins, 6, f.size)
            sqlite3_bind_double(ins, 7, f.modificationDate.timeIntervalSince1970)
            sqlite3_step(ins)
            sqlite3_reset(ins)

            sqlite3_bind_text(fts, 1, (f.sourceId as NSString).utf8String, -1, nil)
            sqlite3_bind_text(fts, 2, (f.name as NSString).utf8String, -1, nil)
            sqlite3_bind_text(fts, 3, (f.path as NSString).utf8String, -1, nil)
            sqlite3_step(fts)
            sqlite3_reset(fts)

            if !f.isDirectory {
                totalFiles += 1
                totalBytes += f.size
            }
        }
        sqlite3_finalize(ins)
        sqlite3_finalize(fts)

        // Upsert source metadata.
        let upSrc = """
            INSERT INTO indexed_sources (source_id, kind, display_name, last_indexed, total_files, total_bytes)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(source_id) DO UPDATE SET
                kind = excluded.kind,
                display_name = excluded.display_name,
                last_indexed = excluded.last_indexed,
                total_files = excluded.total_files,
                total_bytes = excluded.total_bytes
        """
        var up: OpaquePointer?
        sqlite3_prepare_v2(db, upSrc, -1, &up, nil)
        sqlite3_bind_text(up, 1, (sourceId as NSString).utf8String, -1, nil)
        sqlite3_bind_text(up, 2, (kind as NSString).utf8String, -1, nil)
        sqlite3_bind_text(up, 3, (displayName as NSString).utf8String, -1, nil)
        sqlite3_bind_double(up, 4, Date().timeIntervalSince1970)
        sqlite3_bind_int(up, 5, Int32(totalFiles))
        sqlite3_bind_int64(up, 6, totalBytes)
        sqlite3_step(up)
        sqlite3_finalize(up)

        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    /// Drop every indexed_files row for this source plus its source row.
    func dropSource(sourceId: String) {
        guard let db else { return }
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        for sql in [
            "DELETE FROM indexed_files WHERE source_id = ?",
            "DELETE FROM indexed_files_fts WHERE source_id = ?",
            "DELETE FROM indexed_sources WHERE source_id = ?"
        ] {
            var stmt: OpaquePointer?
            sqlite3_prepare_v2(db, sql, -1, &stmt, nil)
            sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, nil)
            sqlite3_step(stmt)
            sqlite3_finalize(stmt)
        }
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    /// Fetch source metadata (used to render "last indexed N days ago").
    func source(_ sourceId: String) -> IndexedSource? {
        guard let db else { return nil }
        let sql = "SELECT source_id, kind, display_name, last_indexed, total_files, total_bytes FROM indexed_sources WHERE source_id = ?"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, nil)
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        let kind = String(cString: sqlite3_column_text(stmt, 1))
        let name = String(cString: sqlite3_column_text(stmt, 2))
        let last: Date? = sqlite3_column_type(stmt, 3) == SQLITE_NULL
            ? nil : Date(timeIntervalSince1970: sqlite3_column_double(stmt, 3))
        return IndexedSource(
            sourceId: sourceId,
            kind: kind,
            displayName: name,
            lastIndexed: last,
            totalFiles: Int(sqlite3_column_int(stmt, 4)),
            totalBytes: sqlite3_column_int64(stmt, 5)
        )
    }

    /// List entries whose parent path equals `parentPath`. Used by the
    /// offline browser to walk an indexed source folder-by-folder.
    func listChildren(sourceId: String, parentPath: String) -> [IndexedFile] {
        guard let db else { return [] }
        let sql = """
            SELECT path, parent_path, name, is_directory, size, modification_date
            FROM indexed_files
            WHERE source_id = ? AND parent_path = ?
            ORDER BY is_directory DESC, name COLLATE NOCASE ASC
        """
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, nil)
        sqlite3_bind_text(stmt, 2, (parentPath as NSString).utf8String, -1, nil)

        var rows: [IndexedFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            rows.append(IndexedFile(
                sourceId: sourceId,
                path: String(cString: sqlite3_column_text(stmt, 0)),
                parentPath: String(cString: sqlite3_column_text(stmt, 1)),
                name: String(cString: sqlite3_column_text(stmt, 2)),
                isDirectory: sqlite3_column_int(stmt, 3) == 1,
                size: sqlite3_column_int64(stmt, 4),
                modificationDate: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 5))
            ))
        }
        return rows
    }

    /// Full-text search of indexed files. If `sourceIds` is non-nil, only
    /// those sources are queried; otherwise all sources are searched.
    func searchIndexed(query: String, sourceIds: [String]? = nil, limit: Int = 500) -> [IndexedFile] {
        guard let db else { return [] }
        let fts = Self.makeFTSQuery(from: query)
        guard !fts.isEmpty else { return [] }

        var sql = """
            SELECT i.source_id, i.path, i.parent_path, i.name, i.is_directory, i.size, i.modification_date
            FROM indexed_files i
            JOIN indexed_files_fts fts ON i.path = fts.path AND i.source_id = fts.source_id
            WHERE fts.name MATCH ?
        """
        if let ids = sourceIds, !ids.isEmpty {
            let placeholders = Array(repeating: "?", count: ids.count).joined(separator: ", ")
            sql += " AND i.source_id IN (\(placeholders))"
        }
        sql += " LIMIT ?"

        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }

        var bindIdx: Int32 = 1
        sqlite3_bind_text(stmt, bindIdx, (fts as NSString).utf8String, -1, nil); bindIdx += 1
        if let ids = sourceIds {
            for id in ids {
                sqlite3_bind_text(stmt, bindIdx, (id as NSString).utf8String, -1, nil)
                bindIdx += 1
            }
        }
        sqlite3_bind_int(stmt, bindIdx, Int32(limit))

        var rows: [IndexedFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            rows.append(IndexedFile(
                sourceId: String(cString: sqlite3_column_text(stmt, 0)),
                path: String(cString: sqlite3_column_text(stmt, 1)),
                parentPath: String(cString: sqlite3_column_text(stmt, 2)),
                name: String(cString: sqlite3_column_text(stmt, 3)),
                isDirectory: sqlite3_column_int(stmt, 4) == 1,
                size: sqlite3_column_int64(stmt, 5),
                modificationDate: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 6))
            ))
        }
        return rows
    }

    /// Returns the union of cached cloud_files matches for the given offline
    /// accounts. Used by SearchCoordinator when "show offline results" is on
    /// and a cloud account is not currently connected.
    func searchCloudCached(query: String, accountIds: [UUID], limit: Int = 500) -> [IndexedCloudFile] {
        guard let db, !accountIds.isEmpty else { return [] }
        let fts = Self.makeFTSQuery(from: query)
        guard !fts.isEmpty else { return [] }

        let placeholders = Array(repeating: "?", count: accountIds.count).joined(separator: ", ")
        let sql = """
            SELECT cf.account_id, cf.path, cf.name, cf.is_directory, cf.size,
                   cf.modification_date, cf.checksum, cf.last_indexed
            FROM cloud_files cf
            JOIN cloud_files_fts fts ON cf.rowid = fts.rowid
            WHERE fts.name MATCH ? AND cf.account_id IN (\(placeholders))
            LIMIT ?
        """

        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }

        var idx: Int32 = 1
        sqlite3_bind_text(stmt, idx, (fts as NSString).utf8String, -1, nil); idx += 1
        for accountId in accountIds {
            sqlite3_bind_text(stmt, idx, (accountId.uuidString as NSString).utf8String, -1, nil)
            idx += 1
        }
        sqlite3_bind_int(stmt, idx, Int32(limit))

        var rows: [IndexedCloudFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            let accId = String(cString: sqlite3_column_text(stmt, 0))
            let path = String(cString: sqlite3_column_text(stmt, 1))
            let name = String(cString: sqlite3_column_text(stmt, 2))
            let isDir = sqlite3_column_int(stmt, 3) == 1
            let size = sqlite3_column_int64(stmt, 4)
            let modDate = Date(timeIntervalSince1970: sqlite3_column_double(stmt, 5))
            let checksum: String? = sqlite3_column_type(stmt, 6) != SQLITE_NULL
                ? String(cString: sqlite3_column_text(stmt, 6)) : nil
            let lastIndexed = Date(timeIntervalSince1970: sqlite3_column_double(stmt, 7))
            let item = CloudFileItem(
                id: path, name: name, path: path,
                isDirectory: isDir, size: size,
                modificationDate: modDate, checksum: checksum
            )
            if let uuid = UUID(uuidString: accId) {
                rows.append(IndexedCloudFile(accountId: uuid, item: item, lastIndexed: lastIndexed))
            }
        }
        return rows
    }

    // MARK: - Query helpers

    /// Builds a safe FTS5 MATCH expression by re-applying the same
    /// tokenization rules unicode61 uses on the indexed content: split on
    /// every non-alphanumeric character, append a `*` to each token for
    /// prefix matching, and AND them together. Without this, a query like
    /// `store.txt` is passed verbatim and never matches files whose name
    /// was tokenized into `store` / `txt`.
    static func makeFTSQuery(from raw: String) -> String {
        let separators = CharacterSet.alphanumerics.inverted
        let tokens = raw.components(separatedBy: separators).filter { !$0.isEmpty }
        return tokens.map { "\($0)*" }.joined(separator: " ")
    }

    // MARK: - Aggregate listing for Settings → Index Status

    /// Snapshot of every indexed source — both drives (in `indexed_sources`)
    /// and cloud accounts (aggregated from `cloud_files`). Used by the
    /// Settings panel to show what's been indexed.
    struct IndexedAccountSummary: Sendable, Hashable, Identifiable {
        let id: UUID
        let lastIndexed: Date
        let totalFiles: Int
        let totalFolders: Int
        let totalBytes: Int64
    }

    func listIndexedSources() -> [IndexedSource] {
        guard let db else { return [] }
        let sql = "SELECT source_id, kind, display_name, last_indexed, total_files, total_bytes FROM indexed_sources ORDER BY display_name COLLATE NOCASE"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }
        var rows: [IndexedSource] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            let last: Date? = sqlite3_column_type(stmt, 3) == SQLITE_NULL
                ? nil : Date(timeIntervalSince1970: sqlite3_column_double(stmt, 3))
            rows.append(IndexedSource(
                sourceId: String(cString: sqlite3_column_text(stmt, 0)),
                kind: String(cString: sqlite3_column_text(stmt, 1)),
                displayName: String(cString: sqlite3_column_text(stmt, 2)),
                lastIndexed: last,
                totalFiles: Int(sqlite3_column_int(stmt, 4)),
                totalBytes: sqlite3_column_int64(stmt, 5)
            ))
        }
        return rows
    }

    /// File/folder counts for one source, used to display granular numbers
    /// in Settings without keeping them in indexed_sources (which already
    /// has total_files counting only non-directories).
    func sourceCounts(_ sourceId: String) -> (files: Int, folders: Int)? {
        guard let db else { return nil }
        let sql = "SELECT SUM(CASE WHEN is_directory=0 THEN 1 ELSE 0 END), SUM(CASE WHEN is_directory=1 THEN 1 ELSE 0 END) FROM indexed_files WHERE source_id = ?"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, nil)
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        return (Int(sqlite3_column_int(stmt, 0)), Int(sqlite3_column_int(stmt, 1)))
    }

    /// Per-account aggregate over the cloud cache. Returns nil if the
    /// account has no rows. Counts non-directory and directory entries
    /// separately, and uses the freshest `last_indexed` as the timestamp.
    func cloudAccountSummary(accountId: UUID) -> IndexedAccountSummary? {
        guard let db else { return nil }
        let sql = """
            SELECT
                MAX(last_indexed),
                SUM(CASE WHEN is_directory = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN is_directory = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN is_directory = 0 THEN size ELSE 0 END)
            FROM cloud_files
            WHERE account_id = ?
        """
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, nil)
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        if sqlite3_column_type(stmt, 0) == SQLITE_NULL { return nil }
        let last = Date(timeIntervalSince1970: sqlite3_column_double(stmt, 0))
        let files = Int(sqlite3_column_int(stmt, 1))
        let folders = Int(sqlite3_column_int(stmt, 2))
        let bytes = sqlite3_column_int64(stmt, 3)
        if files == 0 && folders == 0 { return nil }
        return IndexedAccountSummary(
            id: accountId,
            lastIndexed: last,
            totalFiles: files,
            totalFolders: folders,
            totalBytes: bytes
        )
    }

    /// Bulk read for Compare: every indexed_files row whose absolute path
    /// is at or beneath `rootPath`. Caller computes relative paths.
    func indexedFilesUnder(sourceId: String, rootPath: String) -> [IndexedFile] {
        guard let db else { return [] }
        let prefix = rootPath == "/" ? "/" : (rootPath.hasSuffix("/") ? rootPath : rootPath + "/")
        let sql: String
        if rootPath == "/" {
            sql = """
                SELECT path, parent_path, name, is_directory, size, modification_date
                FROM indexed_files
                WHERE source_id = ?
                ORDER BY path
            """
        } else {
            sql = """
                SELECT path, parent_path, name, is_directory, size, modification_date
                FROM indexed_files
                WHERE source_id = ? AND (path = ? OR path LIKE ? ESCAPE '\\')
                ORDER BY path
            """
        }
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, nil)
        if rootPath != "/" {
            let escaped = Self.escapeLike(prefix)
            sqlite3_bind_text(stmt, 2, (rootPath as NSString).utf8String, -1, nil)
            sqlite3_bind_text(stmt, 3, ((escaped + "%") as NSString).utf8String, -1, nil)
        }
        var rows: [IndexedFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            rows.append(IndexedFile(
                sourceId: sourceId,
                path: String(cString: sqlite3_column_text(stmt, 0)),
                parentPath: String(cString: sqlite3_column_text(stmt, 1)),
                name: String(cString: sqlite3_column_text(stmt, 2)),
                isDirectory: sqlite3_column_int(stmt, 3) == 1,
                size: sqlite3_column_int64(stmt, 4),
                modificationDate: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 5))
            ))
        }
        return rows
    }

    struct CloudIndexedFile: Sendable, Hashable {
        let accountId: UUID
        let path: String
        let name: String
        let isDirectory: Bool
        let size: Int64
        let modificationDate: Date
    }

    /// Bulk read for Compare from `cloud_files` (the cloud index cache).
    func cloudFilesUnder(accountId: UUID, rootPath: String) -> [CloudIndexedFile] {
        guard let db else { return [] }
        let prefix = rootPath == "/" ? "/" : (rootPath.hasSuffix("/") ? rootPath : rootPath + "/")
        let sql: String
        if rootPath == "/" {
            sql = """
                SELECT path, name, is_directory, size, modification_date
                FROM cloud_files
                WHERE account_id = ?
                ORDER BY path
            """
        } else {
            sql = """
                SELECT path, name, is_directory, size, modification_date
                FROM cloud_files
                WHERE account_id = ? AND (path = ? OR path LIKE ? ESCAPE '\\')
                ORDER BY path
            """
        }
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, nil)
        if rootPath != "/" {
            let escaped = Self.escapeLike(prefix)
            sqlite3_bind_text(stmt, 2, (rootPath as NSString).utf8String, -1, nil)
            sqlite3_bind_text(stmt, 3, ((escaped + "%") as NSString).utf8String, -1, nil)
        }
        var rows: [CloudIndexedFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            rows.append(CloudIndexedFile(
                accountId: accountId,
                path: String(cString: sqlite3_column_text(stmt, 0)),
                name: String(cString: sqlite3_column_text(stmt, 1)),
                isDirectory: sqlite3_column_int(stmt, 2) == 1,
                size: sqlite3_column_int64(stmt, 3),
                modificationDate: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 4))
            ))
        }
        return rows
    }

    /// Escapes `%`, `_`, and `\` for use in a SQL `LIKE ... ESCAPE '\\'`
    /// clause, so path segments containing those characters don't act as
    /// wildcards or break the escape.
    private static func escapeLike(_ s: String) -> String {
        var out = ""
        out.reserveCapacity(s.count)
        for ch in s {
            if ch == "\\" || ch == "%" || ch == "_" { out.append("\\") }
            out.append(ch)
        }
        return out
    }

    /// Records a cloud account's indexing in `indexed_sources` so the
    /// Settings → Index Status tab and the right-click context menu can
    /// treat cloud and drive sources uniformly. Called after a successful
    /// walkCloud completes.
    func recordCloudSource(accountId: UUID, displayName: String, summary: IndexedAccountSummary) {
        guard let db else { return }
        // Update `kind` on conflict too — older builds had a bind bug that
        // wrote an empty string for `kind`, so re-indexing must be able to
        // self-heal the row (otherwise the cloud account stays mis-classified
        // as a drive in Settings → Index Status forever).
        let sql = """
            INSERT INTO indexed_sources (source_id, kind, display_name, last_indexed, total_files, total_bytes)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(source_id) DO UPDATE SET
                kind = excluded.kind,
                display_name = excluded.display_name,
                last_indexed = excluded.last_indexed,
                total_files = excluded.total_files,
                total_bytes = excluded.total_bytes
        """
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
        defer { sqlite3_finalize(stmt) }
        // Bind every text via NSString.utf8String — passing a Swift String
        // literal directly to sqlite3_bind_text with a nil destructor
        // (SQLITE_STATIC) lands a dangling pointer here: Swift's auto-bridge
        // to `const char *` only keeps the C buffer alive for the duration
        // of the bind call, but SQLite reads the bytes lazily during step().
        // The result is an empty string in the `kind` column for every
        // cloud account ever indexed. Routing through NSString keeps the
        // backing buffer alive for the scope of the local, which is enough.
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, nil)
        sqlite3_bind_text(stmt, 2, ("cloud" as NSString).utf8String, -1, nil)
        sqlite3_bind_text(stmt, 3, (displayName as NSString).utf8String, -1, nil)
        sqlite3_bind_double(stmt, 4, summary.lastIndexed.timeIntervalSince1970)
        sqlite3_bind_int(stmt, 5, Int32(summary.totalFiles))
        sqlite3_bind_int64(stmt, 6, summary.totalBytes)
        sqlite3_step(stmt)
    }

    /// Drops the cloud cache rows for an account. Used by the "Erase All"
    /// action in Settings → Index Status.
    func dropCloudAccount(accountId: UUID) {
        guard let db else { return }
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        var del: OpaquePointer?
        sqlite3_prepare_v2(db, "DELETE FROM cloud_files WHERE account_id = ?", -1, &del, nil)
        sqlite3_bind_text(del, 1, (accountId.uuidString as NSString).utf8String, -1, nil)
        sqlite3_step(del)
        sqlite3_finalize(del)
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    /// Wipes every indexed row across both unified and cloud-cache tables.
    func wipeAll() {
        guard let db else { return }
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        for sql in [
            "DELETE FROM indexed_files",
            "DELETE FROM indexed_files_fts",
            "DELETE FROM indexed_sources",
            "DELETE FROM cloud_files"
        ] {
            sqlite3_exec(db, sql, nil, nil, nil)
        }
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    private func execute(_ sql: String) throws {
        guard let db else { return }
        var errMsg: UnsafeMutablePointer<CChar>?
        if sqlite3_exec(db, sql, nil, nil, &errMsg) != SQLITE_OK {
            let msg = errMsg.map { String(cString: $0) } ?? "unknown error"
            sqlite3_free(errMsg)
            throw SearchIndexError.executionFailed(msg)
        }
    }

    struct IndexedCloudFile: Sendable {
        let accountId: UUID
        let item: CloudFileItem
        let lastIndexed: Date
    }

    enum SearchIndexError: LocalizedError {
        case openFailed(String)
        case executionFailed(String)

        var errorDescription: String? {
            switch self {
            case .openFailed(let msg): return "Failed to open search index: \(msg)"
            case .executionFailed(let msg): return "Search index error: \(msg)"
            }
        }
    }
}
