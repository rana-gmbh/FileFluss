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

        // Use FTS5 prefix matching
        let ftsQuery = query
            .components(separatedBy: .whitespaces)
            .filter { !$0.isEmpty }
            .map { "\($0)*" }
            .joined(separator: " ")

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
