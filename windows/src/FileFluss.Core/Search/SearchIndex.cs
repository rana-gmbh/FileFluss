// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileFluss.Core.Search;

/// <summary>
/// The offline search index — port of the macOS <c>SearchIndex</c> actor, same schema and
/// semantics, on Microsoft.Data.Sqlite.
///
/// <para><b>Two stores.</b> Cloud accounts live in <c>cloud_files</c> (keyed by account id,
/// fed incrementally by browsing, searching and full indexing runs, with an external-content
/// FTS5 table kept in sync by triggers). Drives live in <c>indexed_files</c> (keyed by an
/// opaque source id, <c>vol:&lt;serial&gt;</c>, replaced wholesale by each indexing run via a
/// staging table). <c>indexed_sources</c> records every source that has been indexed end to
/// end — that, not the mere presence of browsed rows, is what "indexed" means in the UI.</para>
///
/// <para><b>Concurrency.</b> The database runs in WAL mode and every operation uses its own
/// pooled connection, so searches and offline browsing read while an index is being written.
/// Writes are serialized in-process by one lock, so they never contend with each other for
/// SQLite's write lock. All methods are synchronous and thread-safe; call the large ones
/// (staging, promoting, bulk reads) off the UI thread.</para>
///
/// <para><b>Failures.</b> The writes that build an index throw <see cref="SearchIndexException"/>
/// — "Make a failed index write say so". Reads and small maintenance writes log to the
/// support log and return empty, where throwing would gain the caller nothing.</para>
///
/// <para>The database opens itself on first use; nothing depends on a separate
/// <see cref="Open"/> having run first.</para>
/// </summary>
public sealed class SearchIndex : IDisposable
{
    /// <summary><c>indexed_sources.kind</c> for a cloud account; drives use "drive-&lt;kind&gt;".</summary>
    public const string CloudKind = "cloud";

    public const int DefaultSearchLimit = 500;

    private const string LogCategory = "Indexing";

    /// <summary>SQLITE_CONSTRAINT: a row the table refuses (a duplicate path), counted rather than fatal.</summary>
    private const int SqliteConstraint = 19;

    private readonly string _connectionString;
    private readonly Lock _openGate = new();
    private readonly Lock _writeGate = new();
    private volatile bool _isOpen;

    /// <summary>An index at a path of the caller's choosing; the app passes <see cref="DefaultPath"/>, tests a temporary file.</summary>
    public SearchIndex(string path)
    {
        DatabasePath = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary><c>%LOCALAPPDATA%\FileFluss\index.sqlite</c>.</summary>
    public static string DefaultPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileFluss", "index.sqlite");

    public string DatabasePath { get; }

    /// <summary>The <c>source_id</c> a cloud account is recorded under in <c>indexed_sources</c>.</summary>
    public static string SourceIdFor(Guid accountId) => accountId.ToString("D");

    // MARK: - Opening

    /// <summary>
    /// Opens (creating if needed) the database and its schema. Every entry point calls this,
    /// so it never needs calling up front; the app may call it at launch to surface a broken
    /// index early. A failed open is retried on the next use rather than remembered.
    /// </summary>
    public void Open()
    {
        if (_isOpen)
        {
            return;
        }

        lock (_openGate)
        {
            if (_isOpen)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                CreateSchema(connection);
                _isOpen = true;
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
                throw new SearchIndexException(SearchIndexErrorKind.OpenFailed, e.Message, e);
            }
        }
    }

    /// <summary>
    /// Releases the pooled connections (so the file can be deleted or moved). A later call
    /// opens the index again — and, as at launch, drops anything left staged.
    /// </summary>
    public void Close()
    {
        // Only the open gate: a write holds the write gate while it opens, so taking both here
        // could deadlock against it. A connection in use when the pool is cleared is closed
        // as soon as its operation returns it.
        lock (_openGate)
        {
            _isOpen = false;
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }
    }

    public void Dispose() => Close();

    private static void CreateSchema(SqliteConnection connection)
    {
        // WAL lets searches and offline browsing read while an indexing run writes. It is a
        // property of the file, so setting it once is enough.
        Execute(connection, "PRAGMA journal_mode=WAL");

        Execute(connection, """
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
            """);

        Execute(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS cloud_files_fts USING fts5(
                name,
                content=cloud_files,
                content_rowid=rowid
            )
            """);

        // Triggers keep the FTS table in sync with cloud_files.
        Execute(connection, """
            CREATE TRIGGER IF NOT EXISTS cloud_files_ai AFTER INSERT ON cloud_files BEGIN
                INSERT INTO cloud_files_fts(rowid, name) VALUES (new.rowid, new.name);
            END
            """);
        Execute(connection, """
            CREATE TRIGGER IF NOT EXISTS cloud_files_ad AFTER DELETE ON cloud_files BEGIN
                INSERT INTO cloud_files_fts(cloud_files_fts, rowid, name) VALUES('delete', old.rowid, old.name);
            END
            """);
        Execute(connection, """
            CREATE TRIGGER IF NOT EXISTS cloud_files_au AFTER UPDATE ON cloud_files BEGIN
                INSERT INTO cloud_files_fts(cloud_files_fts, rowid, name) VALUES('delete', old.rowid, old.name);
                INSERT INTO cloud_files_fts(rowid, name) VALUES (new.rowid, new.name);
            END
            """);

        // The unified table for drives (and any future index-anything source), keyed by an
        // opaque source_id. Cloud accounts keep using cloud_files above.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS indexed_sources (
                source_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                display_name TEXT NOT NULL,
                last_indexed REAL,
                total_files INTEGER DEFAULT 0,
                total_bytes INTEGER DEFAULT 0
            )
            """);

        Execute(connection, """
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
            """);

        Execute(connection, """
            CREATE INDEX IF NOT EXISTS idx_indexed_files_parent
            ON indexed_files(source_id, parent_path)
            """);

        // Where an index being built lands until it is complete. A separate table rather than
        // a reserved source_id, so nothing else — search, offline browsing, the sources list —
        // can see a half-written index in the meantime.
        Execute(connection, """
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
            """);

        // Anything still staged belongs to a run that didn't finish — a crash, a quit
        // mid-index. It is not an index, so it goes.
        Execute(connection, "DELETE FROM indexed_files_staging");

        Execute(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS indexed_files_fts USING fts5(
                source_id UNINDEXED,
                name,
                path UNINDEXED,
                tokenize='unicode61'
            )
            """);

        // Drives the app has seen (macOS keeps this list in UserDefaults under "drives.known"),
        // so an indexed drive that is unplugged still appears, offline, under Drives.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS known_drives (
                drive_id TEXT PRIMARY KEY,
                volume_id TEXT NOT NULL,
                label TEXT NOT NULL,
                kind TEXT NOT NULL,
                last_root_path TEXT,
                last_indexed REAL,
                total_files INTEGER DEFAULT 0,
                total_bytes INTEGER DEFAULT 0
            )
            """);
    }

    // MARK: - Cloud cache: writes

    /// <summary>
    /// Adds or refreshes cloud entries. Throws when the index can't be written: this is one way
    /// a cloud account's index is built, and a failure here must not pass for success.
    /// </summary>
    public void UpsertItems(IReadOnlyCollection<CloudFileItem> items, Guid accountId)
    {
        if (items.Count == 0)
        {
            return;
        }

        var account = SourceIdFor(accountId);
        var now = IndexDates.ToStored(DateTimeOffset.Now);
        Write("store the entries", (connection, transaction) =>
        {
            UpsertCore(connection, transaction, account, items, now);
            return 0;
        });
    }

    /// <summary>
    /// For the opportunistic feeds — a search, a storage scan — where the user asked for
    /// something else and a failed index write must not interrupt it. Logged rather than
    /// thrown, so it is still on the record: swallowing it was the original sin.
    /// </summary>
    public void UpsertItemsLogging(IReadOnlyCollection<CloudFileItem> items, Guid accountId, string context)
    {
        try
        {
            UpsertItems(items, accountId);
        }
        catch (SearchIndexException e)
        {
            SupportLogger.Shared.Log($"Index write failed during {context}: could not cache {items.Count} entries: {e.Detail}", LogCategory, LogLevel.Error);
        }
    }

    /// <summary>
    /// Records a completed listing of <paramref name="parentPath"/>: its items are added or
    /// refreshed, and anything cached directly under that folder which the listing no longer
    /// contains is retired, with its subtree — "Let a listing retire cache entries, not only
    /// add them". Without this the cache only grows: a file deleted from another device stays
    /// searchable and browsable offline for ever.
    ///
    /// <para>Only call it with a listing that completed. One that failed part-way is not
    /// evidence that anything is gone, and a listing says nothing about the levels below it,
    /// which are left alone.</para>
    /// </summary>
    /// <returns>How many cached entries the listing retired (subtrees included).</returns>
    public int ApplyListing(Guid accountId, string parentPath, IReadOnlyCollection<CloudFileItem> items)
    {
        var account = SourceIdFor(accountId);
        var parent = CloudPath.Normalize(parentPath);
        var now = IndexDates.ToStored(DateTimeOffset.Now);
        return Write("store the listing", (connection, transaction) =>
        {
            UpsertCore(connection, transaction, account, items, now);

            var listed = new HashSet<string>(items.Select(i => i.Path), StringComparer.Ordinal);
            var prefix = parent == "/" ? "/" : parent + "/";
            var stale = new List<string>();
            using (var children = Command(connection, transaction, """
                SELECT path FROM cloud_files
                WHERE account_id = $account AND path > $low AND path < $high
                  AND instr(substr(path, length($low) + 1), '/') = 0
                """))
            {
                children.Parameters.AddWithValue("$account", account);
                children.Parameters.AddWithValue("$low", prefix);
                children.Parameters.AddWithValue("$high", UpperBound(prefix));
                using var reader = children.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!listed.Contains(path))
                    {
                        stale.Add(path);
                    }
                }
            }

            var retired = 0;
            foreach (var path in stale)
            {
                retired += DeleteCloudSubtree(connection, transaction, account, path);
            }

            return retired;
        });
    }

    /// <summary>The browsing feed: <see cref="ApplyListing"/>, logged instead of thrown so a failed write never disturbs the panel.</summary>
    public void ApplyListingLogging(Guid accountId, string parentPath, IReadOnlyCollection<CloudFileItem> items, string context = "browsing")
    {
        try
        {
            ApplyListing(accountId, parentPath, items);
        }
        catch (SearchIndexException e)
        {
            SupportLogger.Shared.Log($"Index write failed during {context}: could not cache {items.Count} entries: {e.Detail}", LogCategory, LogLevel.Error);
        }
    }

    /// <summary>Forgets cached entries (deleted, or moved away by the app), each with its subtree.</summary>
    public void RemoveItems(Guid accountId, IEnumerable<string> paths)
    {
        var account = SourceIdFor(accountId);
        var list = paths.Select(CloudPath.Normalize).ToList();
        if (list.Count == 0)
        {
            return;
        }

        Maintain("remove entries", (connection, transaction) =>
        {
            foreach (var path in list)
            {
                DeleteCloudSubtree(connection, transaction, account, path);
            }
        });
    }

    public void RemoveAllItems(Guid accountId) => DropCloudAccount(accountId);

    private static void UpsertCore(SqliteConnection connection, SqliteTransaction transaction, string account, IReadOnlyCollection<CloudFileItem> items, double now)
    {
        if (items.Count == 0)
        {
            return;
        }

        // An upsert rather than INSERT OR REPLACE: REPLACE deletes the old row without firing
        // the delete trigger (recursive triggers are off), which leaves a stale entry in the
        // external-content FTS table. DO UPDATE fires the update trigger and keeps the rowid.
        using var command = Command(connection, transaction, """
            INSERT INTO cloud_files (account_id, path, name, is_directory, size, modification_date, checksum, last_indexed)
            VALUES ($account, $path, $name, $dir, $size, $mod, $checksum, $now)
            ON CONFLICT(account_id, path) DO UPDATE SET
                name = excluded.name,
                is_directory = excluded.is_directory,
                size = excluded.size,
                modification_date = excluded.modification_date,
                checksum = excluded.checksum,
                last_indexed = excluded.last_indexed
            """);
        command.Parameters.AddWithValue("$account", account);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var dir = command.Parameters.Add("$dir", SqliteType.Integer);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var mod = command.Parameters.Add("$mod", SqliteType.Real);
        var checksum = command.Parameters.Add("$checksum", SqliteType.Text);
        command.Parameters.AddWithValue("$now", now);
        command.Prepare();

        foreach (var item in items)
        {
            path.Value = item.Path;
            name.Value = item.Name;
            dir.Value = item.IsDirectory ? 1 : 0;
            size.Value = item.Size;
            mod.Value = IndexDates.ToStored(item.ModificationDate);
            checksum.Value = (object?)item.Checksum ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private static int DeleteCloudSubtree(SqliteConnection connection, SqliteTransaction transaction, string account, string path)
    {
        using var command = Command(connection, transaction, """
            DELETE FROM cloud_files
            WHERE account_id = $account AND (path = $path OR (path >= $low AND path < $high))
            """);
        var prefix = path == "/" ? "/" : path + "/";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$low", prefix);
        command.Parameters.AddWithValue("$high", UpperBound(prefix));
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// The smallest string above every path that starts with <paramref name="prefix"/> (which
    /// ends in "/"): "/a/" → "/a0", because '0' follows '/' in byte order. Lets a subtree be
    /// read as an index range instead of a LIKE scan over the whole account.
    /// </summary>
    private static string UpperBound(string prefix) => prefix[..^1] + "0";

    // MARK: - Cloud cache: reads

    /// <summary>Name search over the cloud cache, optionally for one account.</summary>
    public IReadOnlyList<IndexedCloudFile> Search(string query, Guid? accountId, int limit = DefaultSearchLimit)
    {
        var fts = MakeFtsQuery(query);
        if (fts.Length == 0)
        {
            return [];
        }

        return Read<IReadOnlyList<IndexedCloudFile>>([], connection =>
        {
            using var command = Command(connection, null, $"""
                SELECT cf.account_id, cf.path, cf.name, cf.is_directory, cf.size, cf.modification_date, cf.checksum, cf.last_indexed
                FROM cloud_files cf
                JOIN cloud_files_fts ON cf.rowid = cloud_files_fts.rowid
                WHERE cloud_files_fts MATCH $query{(accountId is null ? string.Empty : " AND cf.account_id = $account")}
                LIMIT $limit
                """);
            command.Parameters.AddWithValue("$query", fts);
            if (accountId is { } id)
            {
                command.Parameters.AddWithValue("$account", SourceIdFor(id));
            }

            command.Parameters.AddWithValue("$limit", limit);
            return ReadCloudHits(command);
        });
    }

    /// <summary>
    /// The cached matches of the given (offline) accounts — what search shows for a
    /// disconnected account when "Show offline results" is on.
    /// </summary>
    public IReadOnlyList<IndexedCloudFile> SearchCloudCached(string query, IReadOnlyCollection<Guid> accountIds, int limit = DefaultSearchLimit)
    {
        var fts = MakeFtsQuery(query);
        if (fts.Length == 0 || accountIds.Count == 0)
        {
            return [];
        }

        return Read<IReadOnlyList<IndexedCloudFile>>([], connection =>
        {
            var ids = accountIds.ToList();
            var placeholders = string.Join(", ", ids.Select((_, i) => "$a" + i.ToString(CultureInfo.InvariantCulture)));
            using var command = Command(connection, null, $"""
                SELECT cf.account_id, cf.path, cf.name, cf.is_directory, cf.size, cf.modification_date, cf.checksum, cf.last_indexed
                FROM cloud_files cf
                JOIN cloud_files_fts ON cf.rowid = cloud_files_fts.rowid
                WHERE cloud_files_fts MATCH $query AND cf.account_id IN ({placeholders})
                LIMIT $limit
                """);
            command.Parameters.AddWithValue("$query", fts);
            for (var i = 0; i < ids.Count; i++)
            {
                command.Parameters.AddWithValue("$a" + i.ToString(CultureInfo.InvariantCulture), SourceIdFor(ids[i]));
            }

            command.Parameters.AddWithValue("$limit", limit);
            return ReadCloudHits(command);
        });
    }

    private static List<IndexedCloudFile> ReadCloudHits(SqliteCommand command)
    {
        var results = new List<IndexedCloudFile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var accountId))
            {
                continue;
            }

            var path = reader.GetString(1);
            var name = reader.GetString(2);
            var item = new CloudFileItem(
                path,
                name,
                path,
                reader.GetInt64(3) == 1,
                reader.GetInt64(4),
                IndexDates.FromStored(reader.GetDouble(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6))
            {
                IsHidden = CloudFileItem.IsDotHidden(name),
            };
            results.Add(new IndexedCloudFile(accountId, item, IndexDates.FromStored(reader.GetDouble(7))));
        }

        return results;
    }

    /// <summary>
    /// The direct children of <paramref name="parentPath"/> in an account's cache, folders
    /// first — what an offline account shows. The cache has no parent column; children are
    /// the paths one level below the parent's prefix.
    /// </summary>
    public IReadOnlyList<IndexedFile> ListCloudChildren(Guid accountId, string parentPath)
    {
        var account = SourceIdFor(accountId);
        var parent = CloudPath.Normalize(parentPath);
        var prefix = parent == "/" ? "/" : parent + "/";
        return Read<IReadOnlyList<IndexedFile>>([], connection =>
        {
            using var command = Command(connection, null, """
                SELECT path, name, is_directory, size, modification_date
                FROM cloud_files
                WHERE account_id = $account AND path > $low AND path < $high
                  AND instr(substr(path, length($low) + 1), '/') = 0
                ORDER BY is_directory DESC, name COLLATE NOCASE ASC
                """);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$low", prefix);
            command.Parameters.AddWithValue("$high", UpperBound(prefix));
            var rows = new List<IndexedFile>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new IndexedFile(account, reader.GetString(0), parent, reader.GetString(1), reader.GetInt64(2) == 1, reader.GetInt64(3), IndexDates.FromStored(reader.GetDouble(4))));
            }

            return rows;
        });
    }

    /// <summary>One cached entry of an account, or null.</summary>
    public IndexedFile? CloudEntry(Guid accountId, string path)
    {
        var account = SourceIdFor(accountId);
        var normalized = CloudPath.Normalize(path);
        return Read<IndexedFile?>(null, connection =>
        {
            using var command = Command(connection, null, """
                SELECT path, name, is_directory, size, modification_date
                FROM cloud_files WHERE account_id = $account AND path = $path
                """);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$path", normalized);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new IndexedFile(account, reader.GetString(0), CloudPath.Parent(normalized), reader.GetString(1), reader.GetInt64(2) == 1, reader.GetInt64(3), IndexDates.FromStored(reader.GetDouble(4)))
                : null;
        });
    }

    /// <summary>Bytes of the files cached at or beneath <paramref name="path"/> — an offline folder size.</summary>
    public long CloudSizeUnder(Guid accountId, string path)
        => SizeUnder("cloud_files", "account_id", SourceIdFor(accountId), path);

    /// <summary>Bulk read for Compare: every cached row at or beneath <paramref name="rootPath"/>.</summary>
    public IReadOnlyList<CloudIndexedFile> CloudFilesUnder(Guid accountId, string rootPath)
    {
        var account = SourceIdFor(accountId);
        return Read<IReadOnlyList<CloudIndexedFile>>([], connection =>
        {
            using var command = UnderCommand(connection, "SELECT path, name, is_directory, size, modification_date FROM cloud_files", "account_id", account, rootPath);
            var rows = new List<CloudIndexedFile>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new CloudIndexedFile(accountId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1, reader.GetInt64(3), IndexDates.FromStored(reader.GetDouble(4))));
            }

            return rows;
        });
    }

    /// <summary>
    /// Per-account aggregate over the cloud cache, using the freshest <c>last_indexed</c> as
    /// its date; null when the account has no rows.
    /// </summary>
    public IndexedAccountSummary? CloudAccountSummary(Guid accountId)
        => Read<IndexedAccountSummary?>(null, connection => CloudSummaryCore(connection, null, accountId));

    private static IndexedAccountSummary? CloudSummaryCore(SqliteConnection connection, SqliteTransaction? transaction, Guid accountId)
    {
        using var command = Command(connection, transaction, """
            SELECT
                MAX(last_indexed),
                SUM(CASE WHEN is_directory = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN is_directory = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN is_directory = 0 THEN size ELSE 0 END)
            FROM cloud_files
            WHERE account_id = $account
            """);
        command.Parameters.AddWithValue("$account", SourceIdFor(accountId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        var files = (int)ReadInt64OrZero(reader, 1);
        var folders = (int)ReadInt64OrZero(reader, 2);
        if (files == 0 && folders == 0)
        {
            return null;
        }

        return new IndexedAccountSummary(accountId, IndexDates.FromStored(reader.GetDouble(0)), files, folders, ReadInt64OrZero(reader, 3));
    }

    /// <summary>
    /// The account's summary only if a full indexing run has been recorded — browsing upserts
    /// entries for incremental search, but a handful of browsed folders must not read as
    /// "Indexed 12 files, today" (macOS <c>AppState.refreshIndexInfo</c>). The date is that of
    /// the last full run.
    /// </summary>
    public IndexedAccountSummary? FullCloudIndexInfo(Guid accountId)
    {
        if (GetSource(SourceIdFor(accountId)) is not { LastIndexed: { } lastFullIndex })
        {
            return null;
        }

        return CloudAccountSummary(accountId) is { } summary ? summary with { LastIndexed = lastFullIndex } : null;
    }

    // MARK: - Drives: staged writes

    /// <summary>
    /// Adds a batch of rows to the index being built for <paramref name="sourceId"/>. Staged
    /// rows are invisible to search, browsing and the sources list until
    /// <see cref="PromoteStagedFiles"/> swaps them in, so an index under construction is never
    /// half-shown — and an interrupted one leaves the previous index exactly where it was.
    /// </summary>
    /// <returns>How many rows the table refused: a duplicate path is counted, not fatal.</returns>
    public int StageFiles(IReadOnlyCollection<IndexedFile> files, string sourceId)
    {
        if (files.Count == 0)
        {
            return 0;
        }

        return Write("stage the index", (connection, transaction) =>
        {
            using var insert = Command(connection, transaction, """
                INSERT INTO indexed_files_staging (source_id, path, parent_path, name, is_directory, size, modification_date)
                VALUES ($source, $path, $parent, $name, $dir, $size, $mod)
                """);
            insert.Parameters.AddWithValue("$source", sourceId);
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var parent = insert.Parameters.Add("$parent", SqliteType.Text);
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var dir = insert.Parameters.Add("$dir", SqliteType.Integer);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            var mod = insert.Parameters.Add("$mod", SqliteType.Real);
            insert.Prepare();

            var rejected = 0;
            foreach (var file in files)
            {
                path.Value = file.Path;
                parent.Value = file.ParentPath;
                name.Value = file.Name;
                dir.Value = file.IsDirectory ? 1 : 0;
                size.Value = file.Size;
                mod.Value = IndexDates.ToStored(file.ModificationDate);
                try
                {
                    insert.ExecuteNonQuery();
                }
                catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint)
                {
                    rejected++;
                }
            }

            return rejected;
        });
    }

    /// <summary>
    /// Swaps the staged rows in as the index for <paramref name="sourceId"/>. One short
    /// transaction however large the index: rows move with <c>INSERT … SELECT</c>, so nothing
    /// is read back into memory. Until this succeeds the previous index is intact; if it
    /// throws, it is still intact. The counts are read from what the index now holds, not
    /// from what the caller believes it sent.
    /// </summary>
    public IndexWriteResult PromoteStagedFiles(string sourceId, string kind, string displayName, int requested, int rejected)
        => Write("store the index", (connection, transaction) =>
        {
            foreach (var sql in new[]
            {
                "DELETE FROM indexed_files WHERE source_id = $source",
                "DELETE FROM indexed_files_fts WHERE source_id = $source",
                """
                INSERT INTO indexed_files (source_id, path, parent_path, name, is_directory, size, modification_date)
                SELECT source_id, path, parent_path, name, is_directory, size, modification_date
                FROM indexed_files_staging WHERE source_id = $source
                """,
                """
                INSERT INTO indexed_files_fts (source_id, name, path)
                SELECT source_id, name, path FROM indexed_files_staging WHERE source_id = $source
                """,
                "DELETE FROM indexed_files_staging WHERE source_id = $source",
            })
            {
                using var command = Command(connection, transaction, sql);
                command.Parameters.AddWithValue("$source", sourceId);
                command.ExecuteNonQuery();
            }

            var (files, folders, bytes) = CountsCore(connection, transaction, sourceId);

            using (var source = Command(connection, transaction, """
                INSERT INTO indexed_sources (source_id, kind, display_name, last_indexed, total_files, total_bytes)
                VALUES ($source, $kind, $name, $date, $files, $bytes)
                ON CONFLICT(source_id) DO UPDATE SET
                    kind = excluded.kind,
                    display_name = excluded.display_name,
                    last_indexed = excluded.last_indexed,
                    total_files = excluded.total_files,
                    total_bytes = excluded.total_bytes
                """))
            {
                source.Parameters.AddWithValue("$source", sourceId);
                source.Parameters.AddWithValue("$kind", kind);
                source.Parameters.AddWithValue("$name", displayName);
                source.Parameters.AddWithValue("$date", IndexDates.ToStored(DateTimeOffset.Now));
                source.Parameters.AddWithValue("$files", files);
                source.Parameters.AddWithValue("$bytes", bytes);
                source.ExecuteNonQuery();
            }

            return new IndexWriteResult(requested, rejected, files, folders);
        });

    /// <summary>Throws away a half-built index (the run was cancelled or failed); the previous index is untouched.</summary>
    public void DiscardStagedFiles(string sourceId)
        => Maintain("discard the staged index", (connection, transaction) =>
        {
            using var command = Command(connection, transaction, "DELETE FROM indexed_files_staging WHERE source_id = $source");
            command.Parameters.AddWithValue("$source", sourceId);
            command.ExecuteNonQuery();
        });

    /// <summary>
    /// Replaces everything stored for a source in one call — the staged path, for callers
    /// small enough not to need batching. Throws when the index can't be written.
    /// </summary>
    public IndexWriteResult ReplaceFiles(string sourceId, string kind, string displayName, IReadOnlyCollection<IndexedFile> files)
    {
        DiscardStagedFiles(sourceId);
        try
        {
            var rejected = StageFiles(files, sourceId);
            return PromoteStagedFiles(sourceId, kind, displayName, files.Count, rejected);
        }
        catch (SearchIndexException)
        {
            DiscardStagedFiles(sourceId);
            throw;
        }
    }

    private static (int Files, int Folders, long Bytes) CountsCore(SqliteConnection connection, SqliteTransaction? transaction, string sourceId)
    {
        using var command = Command(connection, transaction, """
            SELECT SUM(CASE WHEN is_directory = 0 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN is_directory = 1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN is_directory = 0 THEN size ELSE 0 END)
            FROM indexed_files WHERE source_id = $source
            """);
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ((int)ReadInt64OrZero(reader, 0), (int)ReadInt64OrZero(reader, 1), ReadInt64OrZero(reader, 2))
            : (0, 0, 0);
    }

    // MARK: - Drives: reads

    /// <summary>Entries whose parent path equals <paramref name="parentPath"/>, folders first — offline browsing of a drive.</summary>
    public IReadOnlyList<IndexedFile> ListChildren(string sourceId, string parentPath)
        => Read<IReadOnlyList<IndexedFile>>([], connection =>
        {
            using var command = Command(connection, null, """
                SELECT path, parent_path, name, is_directory, size, modification_date
                FROM indexed_files
                WHERE source_id = $source AND parent_path = $parent
                ORDER BY is_directory DESC, name COLLATE NOCASE ASC
                """);
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$parent", parentPath);
            return ReadIndexedFiles(command, sourceId, 0);
        });

    /// <summary>One indexed entry of a drive, or null.</summary>
    public IndexedFile? IndexedEntry(string sourceId, string path)
        => Read<IndexedFile?>(null, connection =>
        {
            using var command = Command(connection, null, """
                SELECT path, parent_path, name, is_directory, size, modification_date
                FROM indexed_files WHERE source_id = $source AND path = $path
                """);
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$path", path);
            return ReadIndexedFiles(command, sourceId, 0).FirstOrDefault();
        });

    /// <summary>Bytes of the files indexed at or beneath <paramref name="path"/>.</summary>
    public long IndexedSizeUnder(string sourceId, string path)
        => SizeUnder("indexed_files", "source_id", sourceId, path);

    /// <summary>
    /// Full-text name search of indexed drives. Null <paramref name="sourceIds"/> searches
    /// every source; an empty list searches none (macOS searched every source then, which put
    /// online drives' rows among the "offline" results).
    /// </summary>
    public IReadOnlyList<IndexedFile> SearchIndexed(string query, IReadOnlyCollection<string>? sourceIds = null, int limit = DefaultSearchLimit)
    {
        var fts = MakeFtsQuery(query);
        if (fts.Length == 0 || sourceIds is { Count: 0 })
        {
            return [];
        }

        return Read<IReadOnlyList<IndexedFile>>([], connection =>
        {
            var ids = sourceIds?.ToList() ?? [];
            var filter = ids.Count == 0
                ? string.Empty
                : " AND i.source_id IN (" + string.Join(", ", ids.Select((_, i) => "$s" + i.ToString(CultureInfo.InvariantCulture))) + ")";
            using var command = Command(connection, null, $"""
                SELECT i.path, i.parent_path, i.name, i.is_directory, i.size, i.modification_date, i.source_id
                FROM indexed_files i
                JOIN indexed_files_fts ON i.path = indexed_files_fts.path AND i.source_id = indexed_files_fts.source_id
                WHERE indexed_files_fts MATCH $query{filter}
                LIMIT $limit
                """);
            command.Parameters.AddWithValue("$query", fts);
            for (var i = 0; i < ids.Count; i++)
            {
                command.Parameters.AddWithValue("$s" + i.ToString(CultureInfo.InvariantCulture), ids[i]);
            }

            command.Parameters.AddWithValue("$limit", limit);
            return ReadIndexedFiles(command, null, 6);
        });
    }

    /// <summary>Bulk read for Compare: every indexed row at or beneath <paramref name="rootPath"/>.</summary>
    public IReadOnlyList<IndexedFile> IndexedFilesUnder(string sourceId, string rootPath)
        => Read<IReadOnlyList<IndexedFile>>([], connection =>
        {
            using var command = UnderCommand(connection, "SELECT path, parent_path, name, is_directory, size, modification_date FROM indexed_files", "source_id", sourceId, rootPath);
            return ReadIndexedFiles(command, sourceId, 0);
        });

    private static List<IndexedFile> ReadIndexedFiles(SqliteCommand command, string? sourceId, int sourceColumn)
    {
        var rows = new List<IndexedFile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedFile(
                sourceId ?? reader.GetString(sourceColumn),
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) == 1,
                reader.GetInt64(4),
                IndexDates.FromStored(reader.GetDouble(5))));
        }

        return rows;
    }

    /// <summary>
    /// A "rows at or beneath a root" query, ported as macOS writes it: the root itself plus a
    /// <c>LIKE 'root/%'</c> with the root's own <c>%</c>, <c>_</c> and <c>\</c> escaped.
    /// </summary>
    private static SqliteCommand UnderCommand(SqliteConnection connection, string select, string keyColumn, string key, string rootPath)
    {
        SqliteCommand command;
        if (rootPath == "/")
        {
            command = Command(connection, null, $"{select} WHERE {keyColumn} = $key ORDER BY path");
        }
        else
        {
            var prefix = rootPath.EndsWith('/') ? rootPath : rootPath + "/";
            command = Command(connection, null, $"{select} WHERE {keyColumn} = $key AND (path = $root OR path LIKE $like ESCAPE '\\') ORDER BY path");
            command.Parameters.AddWithValue("$root", rootPath);
            command.Parameters.AddWithValue("$like", EscapeLike(prefix) + "%");
        }

        command.Parameters.AddWithValue("$key", key);
        return command;
    }

    private long SizeUnder(string table, string keyColumn, string key, string path)
    {
        var normalized = CloudPath.Normalize(path);
        return Read(0L, connection =>
        {
            using var command = Command(connection, null, normalized == "/"
                ? $"SELECT SUM(size) FROM {table} WHERE {keyColumn} = $key AND is_directory = 0"
                : $"SELECT SUM(size) FROM {table} WHERE {keyColumn} = $key AND is_directory = 0 AND (path = $path OR (path >= $low AND path < $high))");
            command.Parameters.AddWithValue("$key", key);
            if (normalized != "/")
            {
                var prefix = normalized + "/";
                command.Parameters.AddWithValue("$path", normalized);
                command.Parameters.AddWithValue("$low", prefix);
                command.Parameters.AddWithValue("$high", UpperBound(prefix));
            }

            var value = command.ExecuteScalar();
            return value is long total ? total : 0L;
        });
    }

    /// <summary>Escapes <c>%</c>, <c>_</c> and <c>\</c> for <c>LIKE … ESCAPE '\'</c>, so path segments containing them aren't wildcards.</summary>
    private static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    // MARK: - Sources (Settings → Index Status, the sidebar's "Indexed …" line)

    /// <summary>Source metadata (renders "last indexed N days ago"); null when the source was never fully indexed.</summary>
    public IndexedSource? GetSource(string sourceId)
        => Read<IndexedSource?>(null, connection =>
        {
            using var command = Command(connection, null, "SELECT source_id, kind, display_name, last_indexed, total_files, total_bytes FROM indexed_sources WHERE source_id = $source");
            command.Parameters.AddWithValue("$source", sourceId);
            return ReadSources(command).FirstOrDefault();
        });

    public IReadOnlyList<IndexedSource> ListIndexedSources()
        => Read<IReadOnlyList<IndexedSource>>([], connection =>
        {
            using var command = Command(connection, null, "SELECT source_id, kind, display_name, last_indexed, total_files, total_bytes FROM indexed_sources ORDER BY display_name COLLATE NOCASE");
            return ReadSources(command);
        });

    private static List<IndexedSource> ReadSources(SqliteCommand command)
    {
        var rows = new List<IndexedSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedSource(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : IndexDates.FromStored(reader.GetDouble(3)),
                (int)ReadInt64OrZero(reader, 4),
                ReadInt64OrZero(reader, 5)));
        }

        return rows;
    }

    /// <summary>File and folder counts for one drive source; null only when the index can't be read.</summary>
    public (int Files, int Folders)? SourceCounts(string sourceId)
        => Read<(int Files, int Folders)?>(null, connection =>
        {
            var (files, folders, _) = CountsCore(connection, null, sourceId);
            return (files, folders);
        });

    /// <summary>
    /// Everything Settings → Index Status lists, with the counts each kind keeps where it keeps
    /// them, sorted by name. Accounts only ever browsed are deliberately absent: their small
    /// browsed count would pass for a full index.
    /// </summary>
    public IReadOnlyList<IndexStatusEntry> GetIndexStatus()
    {
        var rows = new List<IndexStatusEntry>();
        foreach (var source in ListIndexedSources())
        {
            if (source.IsCloud && Guid.TryParse(source.SourceId, out var accountId))
            {
                rows.Add(CloudAccountSummary(accountId) is { } summary
                    ? new IndexStatusEntry(source.SourceId, source.Kind, source.DisplayName, source.LastIndexed, summary.TotalFiles, summary.TotalFolders, summary.TotalBytes)
                    : new IndexStatusEntry(source.SourceId, source.Kind, source.DisplayName, source.LastIndexed, source.TotalFiles, 0, source.TotalBytes));
            }
            else
            {
                var counts = SourceCounts(source.SourceId) ?? (0, 0);
                rows.Add(new IndexStatusEntry(source.SourceId, source.Kind, source.DisplayName, source.LastIndexed, counts.Files, counts.Folders, source.TotalBytes));
            }
        }

        return rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Records a completed indexing run of a cloud account in <c>indexed_sources</c>, so Index
    /// Status and the sidebar treat cloud and drive sources alike. Updates the kind on
    /// conflict too, so a mis-recorded row heals on the next run.
    /// </summary>
    public void RecordCloudSource(Guid accountId, string displayName, IndexedAccountSummary summary)
        => Maintain("record the source", (connection, transaction) =>
        {
            using var command = Command(connection, transaction, """
                INSERT INTO indexed_sources (source_id, kind, display_name, last_indexed, total_files, total_bytes)
                VALUES ($source, $kind, $name, $date, $files, $bytes)
                ON CONFLICT(source_id) DO UPDATE SET
                    kind = excluded.kind,
                    display_name = excluded.display_name,
                    last_indexed = excluded.last_indexed,
                    total_files = excluded.total_files,
                    total_bytes = excluded.total_bytes
                """);
            command.Parameters.AddWithValue("$source", SourceIdFor(accountId));
            command.Parameters.AddWithValue("$kind", CloudKind);
            command.Parameters.AddWithValue("$name", displayName);
            command.Parameters.AddWithValue("$date", IndexDates.ToStored(summary.LastIndexed));
            command.Parameters.AddWithValue("$files", summary.TotalFiles);
            command.Parameters.AddWithValue("$bytes", summary.TotalBytes);
            command.ExecuteNonQuery();
        });

    /// <summary>Updates only the display name, so a renamed account shows its new name in Index Status without a re-index.</summary>
    public void RenameIndexedSource(string sourceId, string newName)
        => Maintain("rename the source", (connection, transaction) =>
        {
            using var command = Command(connection, transaction, "UPDATE indexed_sources SET display_name = $name WHERE source_id = $source");
            command.Parameters.AddWithValue("$name", newName);
            command.Parameters.AddWithValue("$source", sourceId);
            command.ExecuteNonQuery();
        });

    // MARK: - Removing

    /// <summary>Drops a drive's index and its source row ("Remove Offline Index").</summary>
    public void DropSource(string sourceId)
        => Maintain("drop the source", (connection, transaction) => DeleteSource(connection, transaction, sourceId, includeCloudCache: false));

    /// <summary>Drops an account's cloud cache only ("Remove Offline Index" drops the source row as well).</summary>
    public void DropCloudAccount(Guid accountId)
        => Maintain("drop the account cache", (connection, transaction) =>
        {
            using var command = Command(connection, transaction, "DELETE FROM cloud_files WHERE account_id = $source");
            command.Parameters.AddWithValue("$source", SourceIdFor(accountId));
            command.ExecuteNonQuery();
        });

    /// <summary>
    /// Everything stored for a cloud account: its source row, any unified rows and its cache.
    /// Called when the account is removed — otherwise the rows linger in Index Status under
    /// the captured name.
    /// </summary>
    public void DropIndexedCloudSource(Guid accountId)
        => Maintain("drop the account index", (connection, transaction) => DeleteSource(connection, transaction, SourceIdFor(accountId), includeCloudCache: true));

    /// <summary>
    /// Launch-time cleanup: drops every cloud source (and its cache) whose account no longer
    /// exists — orphans of earlier remove/re-add cycles.
    /// </summary>
    public void PurgeOrphanCloudSources(IReadOnlySet<Guid> keepAccountIds)
        => Maintain("purge orphan sources", (connection, transaction) =>
        {
            var orphans = new List<string>();
            using (var list = Command(connection, transaction, "SELECT source_id FROM indexed_sources WHERE kind = $kind"))
            {
                list.Parameters.AddWithValue("$kind", CloudKind);
                using var reader = list.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!Guid.TryParse(id, out var accountId) || !keepAccountIds.Contains(accountId))
                    {
                        orphans.Add(id);
                    }
                }
            }

            foreach (var orphan in orphans)
            {
                DeleteSource(connection, transaction, orphan, includeCloudCache: true);
            }
        });

    /// <summary>Wipes every indexed row, drives and cloud cache alike ("Erase All Indexes"). The known-drives list stays.</summary>
    public void WipeAll()
        => Maintain("erase the index", (connection, transaction) =>
        {
            foreach (var sql in new[]
            {
                "DELETE FROM indexed_files",
                "DELETE FROM indexed_files_fts",
                "DELETE FROM indexed_sources",
                "DELETE FROM cloud_files",
            })
            {
                using var command = Command(connection, transaction, sql);
                command.ExecuteNonQuery();
            }
        });

    private static void DeleteSource(SqliteConnection connection, SqliteTransaction transaction, string sourceId, bool includeCloudCache)
    {
        var statements = new List<string>
        {
            "DELETE FROM indexed_files WHERE source_id = $source",
            "DELETE FROM indexed_files_fts WHERE source_id = $source",
            "DELETE FROM indexed_sources WHERE source_id = $source",
        };
        if (includeCloudCache)
        {
            statements.Add("DELETE FROM cloud_files WHERE account_id = $source");
        }

        foreach (var sql in statements)
        {
            using var command = Command(connection, transaction, sql);
            command.Parameters.AddWithValue("$source", sourceId);
            command.ExecuteNonQuery();
        }
    }

    // MARK: - Known drives (macOS "drives.known")

    public IReadOnlyList<KnownDrive> LoadKnownDrives()
        => Read<IReadOnlyList<KnownDrive>>([], connection =>
        {
            using var command = Command(connection, null, "SELECT drive_id, volume_id, label, kind, last_root_path, last_indexed, total_files, total_bytes FROM known_drives ORDER BY rowid");
            var drives = new List<KnownDrive>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                drives.Add(new KnownDrive
                {
                    Id = reader.GetString(0),
                    VolumeId = reader.GetString(1),
                    Label = reader.GetString(2),
                    Kind = DriveKinds.FromRawValue(reader.GetString(3)),
                    LastRootPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    LastIndexed = reader.IsDBNull(5) ? null : IndexDates.FromStored(reader.GetDouble(5)),
                    TotalFiles = (int)ReadInt64OrZero(reader, 6),
                    TotalBytes = ReadInt64OrZero(reader, 7),
                });
            }

            return drives;
        });

    /// <summary>Replaces the persisted list of known drives.</summary>
    public void SaveKnownDrives(IReadOnlyCollection<KnownDrive> drives)
        => Maintain("save the known drives", (connection, transaction) =>
        {
            using (var clear = Command(connection, transaction, "DELETE FROM known_drives"))
            {
                clear.ExecuteNonQuery();
            }

            using var insert = Command(connection, transaction, """
                INSERT OR REPLACE INTO known_drives (drive_id, volume_id, label, kind, last_root_path, last_indexed, total_files, total_bytes)
                VALUES ($id, $volume, $label, $kind, $root, $indexed, $files, $bytes)
                """);
            foreach (var drive in drives)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", drive.Id);
                insert.Parameters.AddWithValue("$volume", drive.VolumeId);
                insert.Parameters.AddWithValue("$label", drive.Label);
                insert.Parameters.AddWithValue("$kind", drive.Kind.RawValue());
                insert.Parameters.AddWithValue("$root", (object?)drive.LastRootPath ?? DBNull.Value);
                insert.Parameters.AddWithValue("$indexed", drive.LastIndexed is { } date ? IndexDates.ToStored(date) : DBNull.Value);
                insert.Parameters.AddWithValue("$files", drive.TotalFiles);
                insert.Parameters.AddWithValue("$bytes", drive.TotalBytes);
                insert.ExecuteNonQuery();
            }
        });

    // MARK: - Query helpers

    /// <summary>
    /// A safe FTS5 MATCH expression, built by the rules unicode61 applies to the indexed names:
    /// split on every non-alphanumeric character, make each token a prefix match, AND them.
    /// Without it, "store.txt" is passed verbatim and never matches a name tokenized into
    /// "store" / "txt". Tokens are quoted so that a word like OR or NOT is a word, not an
    /// operator.
    /// </summary>
    public static string MakeFtsQuery(string raw)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in raw.EnumerateRunes())
        {
            if (IsAlphanumeric(rune))
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return string.Join(' ', tokens.Select(t => "\"" + t + "\"*"));
    }

    /// <summary>Foundation's <c>CharacterSet.alphanumerics</c>: letters, marks and numbers.</summary>
    private static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    // MARK: - Plumbing

    private SqliteConnection Connect()
    {
        Open();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException e)
        {
            connection.Dispose();
            throw new SearchIndexException(SearchIndexErrorKind.OpenFailed, e.Message, e);
        }
    }

    /// <summary>One write transaction, serialized with every other write of this index. Throws on failure; the transaction rolls back.</summary>
    private T Write<T>(string what, Func<SqliteConnection, SqliteTransaction, T> body)
    {
        lock (_writeGate)
        {
            using var connection = Connect();
            try
            {
                using var transaction = connection.BeginTransaction();
                var result = body(connection, transaction);
                transaction.Commit();
                return result;
            }
            catch (SqliteException e)
            {
                throw new SearchIndexException(SearchIndexErrorKind.ExecutionFailed, $"could not {what}: {e.Message}", e);
            }
        }
    }

    /// <summary>A small write whose failure is survivable: logged, not thrown.</summary>
    private void Maintain(string what, Action<SqliteConnection, SqliteTransaction> body, [CallerMemberName] string caller = "")
    {
        try
        {
            Write(what, (connection, transaction) =>
            {
                body(connection, transaction);
                return true;
            });
        }
        catch (SearchIndexException e)
        {
            LogFailure(caller, e);
        }
    }

    /// <summary>A read: logged and answered with <paramref name="fallback"/> when the index can't be read.</summary>
    private T Read<T>(T fallback, Func<SqliteConnection, T> body, [CallerMemberName] string caller = "")
    {
        try
        {
            using var connection = Connect();
            return body(connection);
        }
        catch (SearchIndexException e)
        {
            LogFailure(caller, e);
            return fallback;
        }
        catch (SqliteException e)
        {
            LogFailure(caller, e);
            return fallback;
        }
    }

    private static void LogFailure(string caller, Exception e)
        => SupportLogger.Shared.Log($"[index] {caller} failed: {(e is SearchIndexException s ? s.Detail : e.Message)}", LogCategory, LogLevel.Error);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, null, sql);
        command.ExecuteNonQuery();
    }

    private static long ReadInt64OrZero(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);
}
