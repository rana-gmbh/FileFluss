import Foundation
import OSLog
import SQLite3

let searchIndexLog = Logger(subsystem: "com.rana-gmbh.FileFluss", category: "SearchIndex")

/// `SQLITE_TRANSIENT` instructs sqlite3 to make its own copy of the bound
/// bytes before returning. The C macro doesn't import to Swift, so we
/// reconstruct it. Required for every `sqlite3_bind_text` site that passes
/// a Swift-bridged C string — the bridged buffer may be released before
/// `sqlite3_step` runs, leaving SQLite to read freed memory and store
/// garbage (or zero bytes) in the column.
private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

public actor SearchIndex {
    public static let shared = SearchIndex()

    private var db: OpaquePointer?
    private let dbPath: String

    private init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = appSupport.appendingPathComponent("FileFluss", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        self.dbPath = dir.appendingPathComponent("search_index.db").path
    }

    /// An index at a path of the caller's choosing. Exists so tests can work
    /// against a temporary database: with only the shared instance and a
    /// hard-coded Application Support path, none of this code could be
    /// tested, which is how a write path that reported success it never
    /// checked survived as long as it did.
    public init(path: String) {
        self.dbPath = path
    }

    /// Opens the database if it isn't open yet.
    ///
    /// Every entry point calls this, so nothing depends on someone else
    /// having opened it first. It used to be opened once at launch, behind
    /// reconnecting the cloud accounts — and a reconnect that was slow,
    /// or an open that failed, left the handle nil for the rest of the
    /// session while every write silently did nothing.
    private func ensureOpen() throws {
        guard db == nil else { return }
        try open()
    }

    /// The open database, or nil after logging why it couldn't be opened.
    /// For everything whose failure is survivable — reads, and the small
    /// maintenance writes — where throwing would mean changing every
    /// caller. Silence is what it replaces: these used to return as if
    /// nothing had been asked of them.
    private func handle(_ caller: String = #function) -> OpaquePointer? {
        do {
            try ensureOpen()
        } catch {
            searchIndexLog.error("[index] \(caller, privacy: .public) could not open the index: \(error.localizedDescription, privacy: .public)")
            return nil
        }
        return db
    }

    /// The open database for a write, or a thrown error naming the problem.
    private func writeHandle() throws -> OpaquePointer {
        try ensureOpen()
        guard let db else { throw SearchIndexError.openFailed("the index is not open") }
        return db
    }

    public func open() throws {
        guard db == nil else { return }
        guard sqlite3_open(dbPath, &db) == SQLITE_OK else {
            let message = db.map { String(cString: sqlite3_errmsg($0)) } ?? "could not open \(dbPath)"
            // sqlite3_open hands back a handle even when it fails, so that
            // the error can be read off it. Close it: leaving it in place
            // would look open to everything downstream.
            if let db { sqlite3_close(db) }
            db = nil
            throw SearchIndexError.openFailed(message)
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

        // Where an index being built lands until it is complete. Separate
        // table rather than a reserved source_id so nothing else — search,
        // offline browsing, the sources list — can see a half-written index
        // in the meantime.
        try execute("""
            CREATE TABLE IF NOT EXISTS indexed_files_staging (
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

        // Anything still staged belongs to a run that didn't finish — a
        // crash, a quit mid-index. It is not an index, so it goes.
        try execute("DELETE FROM indexed_files_staging")

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

    public func close() {
        if let db {
            sqlite3_close(db)
        }
        db = nil
    }

    /// Adds or refreshes cloud entries.
    ///
    /// Throws for the same reason `replaceFiles` does: this is how a cloud
    /// account's index is built, and a failure here was as silent as the
    /// drive one — the same bug, one table over, simply not reported yet.
    public func upsertItems(_ items: [CloudFileItem], accountId: UUID) throws {
        let db = try writeHandle()
        let accountStr = accountId.uuidString
        let now = Date().timeIntervalSince1970

        let sql = """
            INSERT OR REPLACE INTO cloud_files
            (account_id, path, name, is_directory, size, modification_date, checksum, last_indexed)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        """

        var stmt: OpaquePointer?
        try prepare(db, sql, into: &stmt)
        defer { sqlite3_finalize(stmt) }

        try exec(db, "BEGIN TRANSACTION")
        for item in items {
            sqlite3_bind_text(stmt, 1, (accountStr as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(stmt, 2, (item.path as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(stmt, 3, (item.name as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_int(stmt, 4, item.isDirectory ? 1 : 0)
            sqlite3_bind_int64(stmt, 5, item.size)
            sqlite3_bind_double(stmt, 6, item.modificationDate.timeIntervalSince1970)
            if let checksum = item.checksum {
                sqlite3_bind_text(stmt, 7, (checksum as NSString).utf8String, -1, SQLITE_TRANSIENT)
            } else {
                sqlite3_bind_null(stmt, 7)
            }
            sqlite3_bind_double(stmt, 8, now)
            do {
                try step(db, stmt, "store \(item.name)")
            } catch {
                sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
                throw error
            }
            sqlite3_reset(stmt)
        }
        do {
            try exec(db, "COMMIT")
        } catch {
            sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
            throw error
        }
    }

    /// For the opportunistic feeds — browsing a folder, running a search,
    /// scanning storage — where the user asked for something else and a
    /// failed index write must not interrupt it. Logged rather than thrown,
    /// so it is still on the record: swallowing it was the original sin.
    public func upsertItemsLogging(_ items: [CloudFileItem], accountId: UUID, context: String) {
        do {
            try upsertItems(items, accountId: accountId)
        } catch {
            searchIndexLog.error("[index] \(context, privacy: .public) could not cache \(items.count) entries: \(error.localizedDescription, privacy: .public)")
            SupportLogger.shared.log(
                "Index write failed during \(context): \(error.localizedDescription)",
                category: "Indexing",
                level: .error
            )
        }
    }

    public func removeItems(accountId: UUID, paths: [String]) {
        guard let db = handle() else { return }
        let accountStr = accountId.uuidString
        for path in paths {
            let sql = "DELETE FROM cloud_files WHERE account_id = ? AND path = ?"
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { continue }
            sqlite3_bind_text(stmt, 1, (accountStr as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(stmt, 2, (path as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_step(stmt)
            sqlite3_finalize(stmt)
        }
    }

    public func removeAllItems(accountId: UUID) {
        guard let db = handle() else { return }
        let sql = "DELETE FROM cloud_files WHERE account_id = ?"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_step(stmt)
        sqlite3_finalize(stmt)
    }

    public func search(query: String, accountId: UUID?, limit: Int = 500) -> [IndexedCloudFile] {
        guard let db = handle() else { return [] }

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

        sqlite3_bind_text(stmt, 1, (ftsQuery as NSString).utf8String, -1, SQLITE_TRANSIENT)
        if let accountId {
            sqlite3_bind_text(stmt, 2, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
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

    public struct IndexedFile: Sendable, Hashable, Identifiable {
        public let sourceId: String
        public let path: String
        public let parentPath: String
        public let name: String
        public let isDirectory: Bool
        public let size: Int64
        public let modificationDate: Date

        public var id: String { "\(sourceId)|\(path)" }

        public init(sourceId: String, path: String, parentPath: String, name: String, isDirectory: Bool, size: Int64, modificationDate: Date) {
            self.sourceId = sourceId
            self.path = path
            self.parentPath = parentPath
            self.name = name
            self.isDirectory = isDirectory
            self.size = size
            self.modificationDate = modificationDate
        }
    }

    public struct IndexedSource: Sendable, Hashable {
        public let sourceId: String
        public let kind: String
        public let displayName: String
        public let lastIndexed: Date?
        public let totalFiles: Int
        public let totalBytes: Int64
    }

    /// Replace this source's stored file rows with `files`. Caller is
    /// responsible for providing a fully-walked snapshot; partial updates
    /// would corrupt the index. Runs in a single transaction.
    /// Adds a batch of rows to the index being built for `sourceId`.
    ///
    /// Staged rows are invisible to search, browsing and the sources list
    /// until `promoteStagedFiles` swaps them in, so an index under
    /// construction is never half-shown — and an interrupted one leaves the
    /// previous index exactly where it was.
    ///
    /// Returns how many rows the table refused: a duplicate path against the
    /// (source_id, path) key is counted, not fatal.
    @discardableResult
    public func stageFiles(_ files: [IndexedFile], sourceId: String) throws -> Int {
        guard !files.isEmpty else { return 0 }
        let db = try writeHandle()
        try exec(db, "BEGIN TRANSACTION")
        do {
            let insSQL = """
                INSERT INTO indexed_files_staging (source_id, path, parent_path, name, is_directory, size, modification_date)
                VALUES (?, ?, ?, ?, ?, ?, ?)
            """
            var ins: OpaquePointer?
            try prepare(db, insSQL, into: &ins)
            defer { sqlite3_finalize(ins) }

            var rejected = 0
            for f in files {
                sqlite3_bind_text(ins, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
                sqlite3_bind_text(ins, 2, (f.path as NSString).utf8String, -1, SQLITE_TRANSIENT)
                sqlite3_bind_text(ins, 3, (f.parentPath as NSString).utf8String, -1, SQLITE_TRANSIENT)
                sqlite3_bind_text(ins, 4, (f.name as NSString).utf8String, -1, SQLITE_TRANSIENT)
                sqlite3_bind_int(ins, 5, f.isDirectory ? 1 : 0)
                sqlite3_bind_int64(ins, 6, f.size)
                sqlite3_bind_double(ins, 7, f.modificationDate.timeIntervalSince1970)
                if sqlite3_step(ins) != SQLITE_DONE { rejected += 1 }
                sqlite3_reset(ins)
            }
            try exec(db, "COMMIT")
            return rejected
        } catch {
            sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
            throw error
        }
    }

    /// Swaps the staged rows in as the index for `sourceId`.
    ///
    /// One short transaction, however large the index: the rows move with
    /// `INSERT ... SELECT`, so nothing is read back into memory to be
    /// written again. Until this succeeds the previous index is intact; if
    /// it throws, it is still intact.
    @discardableResult
    public func promoteStagedFiles(sourceId: String, kind: String, displayName: String, requested: Int, rejected: Int) throws -> IndexWriteResult {
        let db = try writeHandle()
        try exec(db, "BEGIN TRANSACTION")
        do {
            for sql in [
                "DELETE FROM indexed_files WHERE source_id = ?",
                "DELETE FROM indexed_files_fts WHERE source_id = ?"
            ] {
                var del: OpaquePointer?
                try prepare(db, sql, into: &del)
                sqlite3_bind_text(del, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
                try step(db, del, "clear the previous index")
                sqlite3_finalize(del)
            }

            var move: OpaquePointer?
            try prepare(db, """
                INSERT INTO indexed_files (source_id, path, parent_path, name, is_directory, size, modification_date)
                SELECT source_id, path, parent_path, name, is_directory, size, modification_date
                FROM indexed_files_staging WHERE source_id = ?
            """, into: &move)
            sqlite3_bind_text(move, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
            try step(db, move, "store the index")
            sqlite3_finalize(move)

            var moveFts: OpaquePointer?
            try prepare(db, """
                INSERT INTO indexed_files_fts (source_id, name, path)
                SELECT source_id, name, path FROM indexed_files_staging WHERE source_id = ?
            """, into: &moveFts)
            sqlite3_bind_text(moveFts, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
            try step(db, moveFts, "store the search index")
            sqlite3_finalize(moveFts)

            var clear: OpaquePointer?
            try prepare(db, "DELETE FROM indexed_files_staging WHERE source_id = ?", into: &clear)
            sqlite3_bind_text(clear, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
            try step(db, clear, "clear the staging area")
            sqlite3_finalize(clear)

            // Counted from what is now in the index rather than from what
            // the caller believes it sent.
            let counts = countsInTransaction(db, sourceId: sourceId)

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
            try prepare(db, upSrc, into: &up)
            sqlite3_bind_text(up, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(up, 2, (kind as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(up, 3, (displayName as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_double(up, 4, Date().timeIntervalSince1970)
            sqlite3_bind_int(up, 5, Int32(counts.files))
            sqlite3_bind_int64(up, 6, counts.bytes)
            try step(db, up, "record the source")
            sqlite3_finalize(up)

            try exec(db, "COMMIT")

            return IndexWriteResult(
                requested: requested,
                rejected: rejected,
                storedFiles: counts.files,
                storedFolders: counts.folders
            )
        } catch {
            sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
            throw error
        }
    }

    /// Throws away a half-built index — the run was cancelled or failed.
    /// The previous index is untouched by this.
    public func discardStagedFiles(sourceId: String) {
        guard let db = handle() else { return }
        var del: OpaquePointer?
        guard sqlite3_prepare_v2(db, "DELETE FROM indexed_files_staging WHERE source_id = ?", -1, &del, nil) == SQLITE_OK else { return }
        sqlite3_bind_text(del, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_step(del)
        sqlite3_finalize(del)
    }

    private func countsInTransaction(_ db: OpaquePointer, sourceId: String) -> (files: Int, folders: Int, bytes: Int64) {
        let sql = """
            SELECT SUM(CASE WHEN is_directory=0 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN is_directory=1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN is_directory=0 THEN size ELSE 0 END)
            FROM indexed_files WHERE source_id = ?
        """
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return (0, 0, 0) }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
        guard sqlite3_step(stmt) == SQLITE_ROW else { return (0, 0, 0) }
        return (Int(sqlite3_column_int(stmt, 0)), Int(sqlite3_column_int(stmt, 1)), sqlite3_column_int64(stmt, 2))
    }

    /// Replaces everything stored for a source in one call — the staged
    /// path above, for callers small enough not to need batching.
    ///
    /// Throws when the index can't be written, which it could not do
    /// before: every sqlite result was discarded, including the COMMIT, so
    /// a write that never happened was indistinguishable from one that did,
    /// and the caller went on to record "indexed N files". A user indexed
    /// 179,000 files from an SMB share and ended up with an empty database
    /// and no indication anything had gone wrong.
    @discardableResult
    public func replaceFiles(sourceId: String, kind: String, displayName: String, files: [IndexedFile]) throws -> IndexWriteResult {
        discardStagedFiles(sourceId: sourceId)
        do {
            let rejected = try stageFiles(files, sourceId: sourceId)
            return try promoteStagedFiles(
                sourceId: sourceId, kind: kind, displayName: displayName,
                requested: files.count, rejected: rejected
            )
        } catch {
            discardStagedFiles(sourceId: sourceId)
            throw error
        }
    }

    /// What a write actually did.
    public struct IndexWriteResult: Sendable, Equatable {
        /// Rows handed to the index.
        public let requested: Int
        /// Rows the table refused, individually.
        public let rejected: Int
        public let storedFiles: Int
        public let storedFolders: Int

        public var stored: Int { storedFiles + storedFolders }

        /// True when everything offered is now in the index.
        public var isComplete: Bool { rejected == 0 && stored == requested }
    }

    /// Drop every indexed_files row for this source plus its source row.
    public func dropSource(sourceId: String) {
        guard let db = handle() else { return }
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        for sql in [
            "DELETE FROM indexed_files WHERE source_id = ?",
            "DELETE FROM indexed_files_fts WHERE source_id = ?",
            "DELETE FROM indexed_sources WHERE source_id = ?"
        ] {
            var stmt: OpaquePointer?
            sqlite3_prepare_v2(db, sql, -1, &stmt, nil)
            sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_step(stmt)
            sqlite3_finalize(stmt)
        }
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    /// Fetch source metadata (used to render "last indexed N days ago").
    public func source(_ sourceId: String) -> IndexedSource? {
        guard let db = handle() else { return nil }
        let sql = "SELECT source_id, kind, display_name, last_indexed, total_files, total_bytes FROM indexed_sources WHERE source_id = ?"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
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

    /// Cloud equivalent of `listChildren`. Cloud accounts store their
    /// entries in `cloud_files`, where `parent_path` isn't an indexed
    /// column (the drive table has it explicitly). Derive it from `path`
    /// in SQL so the offline cloud browser can drill folder by folder
    /// without changing the schema.
    public func listCloudChildren(accountId: UUID, parentPath: String) -> [IndexedFile] {
        guard let db = handle() else { return [] }
        // `path` is always "/foo/bar"; the parent path is everything up
        // to (but not including) the last "/". Special-case root because
        // rfind('/') in a top-level path like "/file.txt" returns position 1,
        // whose substr(... , 1, 0) is empty — but the intended parent is "/".
        let sql = """
            SELECT path, name, is_directory, size, modification_date
            FROM cloud_files
            WHERE account_id = ?
              AND (
                CASE WHEN instr(substr(path, 2), '/') = 0 THEN '/'
                     ELSE substr(path, 1, length(path) - length(name) - 1)
                END
              ) = ?
            ORDER BY is_directory DESC, name COLLATE NOCASE ASC
        """
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_bind_text(stmt, 2, (parentPath as NSString).utf8String, -1, SQLITE_TRANSIENT)

        var rows: [IndexedFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            let path = String(cString: sqlite3_column_text(stmt, 0))
            let name = String(cString: sqlite3_column_text(stmt, 1))
            rows.append(IndexedFile(
                sourceId: accountId.uuidString,
                path: path,
                parentPath: parentPath,
                name: name,
                isDirectory: sqlite3_column_int(stmt, 2) == 1,
                size: sqlite3_column_int64(stmt, 3),
                modificationDate: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 4))
            ))
        }
        return rows
    }

    /// List entries whose parent path equals `parentPath`. Used by the
    /// offline browser to walk an indexed source folder-by-folder.
    public func listChildren(sourceId: String, parentPath: String) -> [IndexedFile] {
        guard let db = handle() else { return [] }
        let sql = """
            SELECT path, parent_path, name, is_directory, size, modification_date
            FROM indexed_files
            WHERE source_id = ? AND parent_path = ?
            ORDER BY is_directory DESC, name COLLATE NOCASE ASC
        """
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_bind_text(stmt, 2, (parentPath as NSString).utf8String, -1, SQLITE_TRANSIENT)

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
    public func searchIndexed(query: String, sourceIds: [String]? = nil, limit: Int = 500) -> [IndexedFile] {
        guard let db = handle() else { return [] }
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
        sqlite3_bind_text(stmt, bindIdx, (fts as NSString).utf8String, -1, SQLITE_TRANSIENT); bindIdx += 1
        if let ids = sourceIds {
            for id in ids {
                sqlite3_bind_text(stmt, bindIdx, (id as NSString).utf8String, -1, SQLITE_TRANSIENT)
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
    public func searchCloudCached(query: String, accountIds: [UUID], limit: Int = 500) -> [IndexedCloudFile] {
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
        sqlite3_bind_text(stmt, idx, (fts as NSString).utf8String, -1, SQLITE_TRANSIENT); idx += 1
        for accountId in accountIds {
            sqlite3_bind_text(stmt, idx, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
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
    public static func makeFTSQuery(from raw: String) -> String {
        let separators = CharacterSet.alphanumerics.inverted
        let tokens = raw.components(separatedBy: separators).filter { !$0.isEmpty }
        return tokens.map { "\($0)*" }.joined(separator: " ")
    }

    // MARK: - Aggregate listing for Settings → Index Status

    /// Snapshot of every indexed source — both drives (in `indexed_sources`)
    /// and cloud accounts (aggregated from `cloud_files`). Used by the
    /// Settings panel to show what's been indexed.
    public struct IndexedAccountSummary: Sendable, Hashable, Identifiable {
        public let id: UUID
        public let lastIndexed: Date
        public let totalFiles: Int
        public let totalFolders: Int
        public let totalBytes: Int64
    }

    public func listIndexedSources() -> [IndexedSource] {
        guard let db = handle() else { return [] }
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
    public func sourceCounts(_ sourceId: String) -> (files: Int, folders: Int)? {
        guard let db = handle() else { return nil }
        let sql = "SELECT SUM(CASE WHEN is_directory=0 THEN 1 ELSE 0 END), SUM(CASE WHEN is_directory=1 THEN 1 ELSE 0 END) FROM indexed_files WHERE source_id = ?"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        return (Int(sqlite3_column_int(stmt, 0)), Int(sqlite3_column_int(stmt, 1)))
    }

    /// Per-account aggregate over the cloud cache. Returns nil if the
    /// account has no rows. Counts non-directory and directory entries
    /// separately, and uses the freshest `last_indexed` as the timestamp.
    public func cloudAccountSummary(accountId: UUID) -> IndexedAccountSummary? {
        guard let db = handle() else { return nil }
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
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
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
    public func indexedFilesUnder(sourceId: String, rootPath: String) -> [IndexedFile] {
        guard let db = handle() else { return [] }
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
        sqlite3_bind_text(stmt, 1, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
        if rootPath != "/" {
            let escaped = Self.escapeLike(prefix)
            sqlite3_bind_text(stmt, 2, (rootPath as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(stmt, 3, ((escaped + "%") as NSString).utf8String, -1, SQLITE_TRANSIENT)
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

    public struct CloudIndexedFile: Sendable, Hashable {
        public let accountId: UUID
        public let path: String
        public let name: String
        public let isDirectory: Bool
        public let size: Int64
        public let modificationDate: Date
    }

    /// Bulk read for Compare from `cloud_files` (the cloud index cache).
    public func cloudFilesUnder(accountId: UUID, rootPath: String) -> [CloudIndexedFile] {
        guard let db = handle() else { return [] }
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
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
        if rootPath != "/" {
            let escaped = Self.escapeLike(prefix)
            sqlite3_bind_text(stmt, 2, (rootPath as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(stmt, 3, ((escaped + "%") as NSString).utf8String, -1, SQLITE_TRANSIENT)
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
    public func recordCloudSource(accountId: UUID, displayName: String, summary: IndexedAccountSummary) {
        guard let db = handle() else { return }
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
        sqlite3_bind_text(stmt, 1, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_bind_text(stmt, 2, ("cloud" as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_bind_text(stmt, 3, (displayName as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_bind_double(stmt, 4, summary.lastIndexed.timeIntervalSince1970)
        sqlite3_bind_int(stmt, 5, Int32(summary.totalFiles))
        sqlite3_bind_int64(stmt, 6, summary.totalBytes)
        sqlite3_step(stmt)
    }

    /// Drop the `indexed_sources` row, the `indexed_files` rows and the
    /// `cloud_files` cache for one cloud account. Called when the user
    /// removes the account — otherwise these rows stick around as orphans
    /// in Settings → Index Status with the captured display name.
    public func dropIndexedCloudSource(accountId: UUID) {
        guard let db = handle() else { return }
        let uuid = accountId.uuidString
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        for sql in [
            "DELETE FROM indexed_sources WHERE source_id = ?",
            "DELETE FROM indexed_files WHERE source_id = ?",
            "DELETE FROM indexed_files_fts WHERE source_id = ?",
            "DELETE FROM cloud_files WHERE account_id = ?"
        ] {
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { continue }
            sqlite3_bind_text(stmt, 1, (uuid as NSString).utf8String, -1, SQLITE_TRANSIENT)
            sqlite3_step(stmt)
            sqlite3_finalize(stmt)
        }
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    /// One-shot cleanup: delete cloud-kind `indexed_sources` rows whose
    /// source_id isn't in `keepIds`. Used at app launch to clear orphans
    /// left behind by earlier remove/re-add cycles, before this build
    /// started dropping them on account removal.
    public func purgeOrphanCloudSources(keepAccountIds: Set<UUID>) {
        guard let db = handle() else { return }
        let keep = Set(keepAccountIds.map { $0.uuidString })
        // Collect candidate orphans first so we can then run the same
        // drop logic across all four tables — keeps the per-row teardown
        // identical to dropIndexedCloudSource.
        var orphans: [String] = []
        var stmt: OpaquePointer?
        let listSQL = "SELECT source_id FROM indexed_sources WHERE kind = 'cloud'"
        if sqlite3_prepare_v2(db, listSQL, -1, &stmt, nil) == SQLITE_OK {
            while sqlite3_step(stmt) == SQLITE_ROW {
                let id = String(cString: sqlite3_column_text(stmt, 0))
                if !keep.contains(id) {
                    orphans.append(id)
                }
            }
            sqlite3_finalize(stmt)
        }
        guard !orphans.isEmpty else { return }
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        for orphanId in orphans {
            for sql in [
                "DELETE FROM indexed_sources WHERE source_id = ?",
                "DELETE FROM indexed_files WHERE source_id = ?",
                "DELETE FROM indexed_files_fts WHERE source_id = ?",
                "DELETE FROM cloud_files WHERE account_id = ?"
            ] {
                var del: OpaquePointer?
                guard sqlite3_prepare_v2(db, sql, -1, &del, nil) == SQLITE_OK else { continue }
                sqlite3_bind_text(del, 1, (orphanId as NSString).utf8String, -1, SQLITE_TRANSIENT)
                sqlite3_step(del)
                sqlite3_finalize(del)
            }
        }
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    /// Update only the display name for an indexed source. Called when the
    /// user renames a cloud account so Settings → Index Status shows the
    /// new name without waiting for a re-index.
    public func renameIndexedSource(sourceId: String, to newName: String) {
        guard let db = handle() else { return }
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, "UPDATE indexed_sources SET display_name = ? WHERE source_id = ?", -1, &stmt, nil) == SQLITE_OK else { return }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, (newName as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_bind_text(stmt, 2, (sourceId as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_step(stmt)
    }

    /// Drops the cloud cache rows for an account. Used by the "Erase All"
    /// action in Settings → Index Status.
    public func dropCloudAccount(accountId: UUID) {
        guard let db = handle() else { return }
        sqlite3_exec(db, "BEGIN TRANSACTION", nil, nil, nil)
        var del: OpaquePointer?
        sqlite3_prepare_v2(db, "DELETE FROM cloud_files WHERE account_id = ?", -1, &del, nil)
        sqlite3_bind_text(del, 1, (accountId.uuidString as NSString).utf8String, -1, SQLITE_TRANSIENT)
        sqlite3_step(del)
        sqlite3_finalize(del)
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
    }

    /// Wipes every indexed row across both unified and cloud-cache tables.
    public func wipeAll() {
        guard let db = handle() else { return }
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

    // MARK: - Checked sqlite helpers
    //
    // Thin wrappers that turn a return code into an error carrying SQLite's
    // own message. The write paths used to discard every one of these.

    private func exec(_ db: OpaquePointer, _ sql: String) throws {
        guard sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else {
            throw SearchIndexError.executionFailed("\(sql): \(String(cString: sqlite3_errmsg(db)))")
        }
    }

    private func prepare(_ db: OpaquePointer, _ sql: String, into stmt: inout OpaquePointer?) throws {
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else {
            throw SearchIndexError.executionFailed("\(sql): \(String(cString: sqlite3_errmsg(db)))")
        }
    }

    private func step(_ db: OpaquePointer, _ stmt: OpaquePointer?, _ what: String) throws {
        let rc = sqlite3_step(stmt)
        guard rc == SQLITE_DONE || rc == SQLITE_ROW else {
            throw SearchIndexError.executionFailed("could not \(what): \(String(cString: sqlite3_errmsg(db)))")
        }
    }

    private func execute(_ sql: String) throws {
        guard let db = handle() else { return }
        var errMsg: UnsafeMutablePointer<CChar>?
        if sqlite3_exec(db, sql, nil, nil, &errMsg) != SQLITE_OK {
            let msg = errMsg.map { String(cString: $0) } ?? "unknown error"
            sqlite3_free(errMsg)
            throw SearchIndexError.executionFailed(msg)
        }
    }

    public struct IndexedCloudFile: Sendable {
        public let accountId: UUID
        public let item: CloudFileItem
        public let lastIndexed: Date
    }

    public enum SearchIndexError: LocalizedError {
        case openFailed(String)
        case executionFailed(String)

        public var errorDescription: String? {
            switch self {
            case .openFailed(let msg): return "Failed to open search index: \(msg)"
            case .executionFailed(let msg): return "Search index error: \(msg)"
            }
        }
    }
}
