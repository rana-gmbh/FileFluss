import Testing
import Foundation
@testable import FileFlussCore

/// Writing an index, and finding out when it didn't work.
///
/// A user indexed 179,000 files from an SMB share: the sidebar reported
/// them, the database held none, and nothing anywhere said so. Every sqlite
/// result in the write path was discarded, and there was no way to open an
/// index at a test path to notice.
@Suite("Search index writes")
struct SearchIndexWriteTests {

    private func temporaryIndexPath() -> String {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("index-test-\(UUID().uuidString).db")
            .path
    }

    private func files(_ count: Int, sourceId: String, folders: Int = 0) -> [SearchIndex.IndexedFile] {
        (0..<count).map { i in
            SearchIndex.IndexedFile(
                sourceId: sourceId,
                path: "/dir/file-\(i).txt",
                parentPath: "/dir",
                name: "file-\(i).txt",
                isDirectory: i < folders,
                size: 10,
                modificationDate: Date(timeIntervalSince1970: 1_700_000_000)
            )
        }
    }

    @Test("What goes in can be read back, and is reported honestly")
    func roundTrip() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = SearchIndex(path: path)

        let result = try await index.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            files: files(500, sourceId: "vol:TEST", folders: 20)
        )

        #expect(result.requested == 500)
        #expect(result.rejected == 0)
        #expect(result.stored == 500)
        #expect(result.storedFolders == 20)
        #expect(result.isComplete)

        let sources = await index.listIndexedSources()
        #expect(sources.count == 1)
        #expect(sources.first?.sourceId == "vol:TEST")
        let counts = await index.sourceCounts("vol:TEST")
        #expect(counts?.files == 480)
        #expect(counts?.folders == 20)
    }

    /// The index opens itself when it is used. It used to be opened once at
    /// launch, behind reconnecting the cloud accounts, and anything that ran
    /// before that — or after a failed open — wrote into nothing.
    @Test("No separate open() call is needed")
    func opensOnFirstUse() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = SearchIndex(path: path)

        // Note: no `try await index.open()` anywhere in this test.
        try await index.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-external", displayName: "Disk",
            files: files(3, sourceId: "vol:TEST")
        )

        #expect(await index.sourceCounts("vol:TEST")?.files == 3)
        #expect(FileManager.default.fileExists(atPath: path))
    }

    /// The failure the user hit, in the only form a test can stage: an index
    /// that cannot be opened must say so, not return as if it had worked.
    @Test("An index that can't be written reports it instead of pretending")
    func unwritableIndexThrows() async {
        // A directory that doesn't exist — sqlite can't create a file there.
        let path = "/nonexistent-\(UUID().uuidString)/search_index.db"
        let index = SearchIndex(path: path)

        await #expect(throws: (any Error).self) {
            try await index.replaceFiles(
                sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
                files: files(10, sourceId: "vol:TEST")
            )
        }
    }

    @Test("A cloud write reports failure too")
    func unwritableCloudIndexThrows() async {
        let path = "/nonexistent-\(UUID().uuidString)/search_index.db"
        let index = SearchIndex(path: path)
        let item = CloudFileItem(
            id: "1", name: "a.txt", path: "/a.txt", isDirectory: false,
            size: 1, modificationDate: Date(), checksum: nil
        )

        await #expect(throws: (any Error).self) {
            try await index.upsertItems([item], accountId: UUID())
        }
    }

    /// A row the table refuses — two entries claiming the same path —
    /// shouldn't throw the whole index away, but the shortfall has to be
    /// visible rather than guessed at from a file count.
    @Test("A rejected row is counted, not fatal")
    func duplicatePathsAreCounted() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = SearchIndex(path: path)

        let duplicate = SearchIndex.IndexedFile(
            sourceId: "vol:TEST", path: "/same.txt", parentPath: "/",
            name: "same.txt", isDirectory: false, size: 1, modificationDate: Date()
        )

        let result = try await index.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            files: [duplicate, duplicate]
        )

        #expect(result.requested == 2)
        #expect(result.rejected == 1)
        #expect(result.stored == 1)
        #expect(!result.isComplete)
    }

    @Test("Indexing again replaces what was there")
    func reindexReplaces() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = SearchIndex(path: path)

        try await index.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            files: files(100, sourceId: "vol:TEST")
        )
        let second = try await index.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            files: files(5, sourceId: "vol:TEST")
        )

        #expect(second.stored == 5)
        #expect(await index.sourceCounts("vol:TEST")?.files == 5)
        #expect(await index.listIndexedSources().count == 1)
    }

    // MARK: Batched writes

    /// The walk feeds rows in batches instead of holding a whole drive in
    /// memory. The result has to be the same index either way.
    @Test("Rows written in batches land as one index")
    func batchedWriteMatchesOneShot() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = SearchIndex(path: path)
        let all = files(250, sourceId: "vol:TEST", folders: 10)

        var rejected = 0
        for chunk in stride(from: 0, to: all.count, by: 60).map({ Array(all[$0..<min($0 + 60, all.count)]) }) {
            rejected += try await index.stageFiles(chunk, sourceId: "vol:TEST")
        }
        let result = try await index.promoteStagedFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            requested: all.count, rejected: rejected
        )

        #expect(result.stored == 250)
        #expect(result.isComplete)
        #expect(await index.sourceCounts("vol:TEST")?.files == 240)
    }

    /// Half a walk is not an index. Until it is promoted, nothing can see
    /// it — and what was there before is still what everything sees.
    @Test("A half-written index is invisible, and doesn't disturb the old one")
    func stagedRowsAreInvisible() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = SearchIndex(path: path)

        try await index.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            files: files(10, sourceId: "vol:TEST")
        )

        // A new run starts and gets part way.
        try await index.stageFiles(files(400, sourceId: "vol:TEST"), sourceId: "vol:TEST")

        // Everything still reports the index from before.
        #expect(await index.sourceCounts("vol:TEST")?.files == 10)
        #expect(await index.listIndexedSources().count == 1)
        #expect(await index.listChildren(sourceId: "vol:TEST", parentPath: "/dir").count == 10)
    }

    /// A cancelled or failed run throws its staged rows away, and the
    /// previous index survives it untouched.
    @Test("Discarding a half-written index leaves the previous one")
    func discardKeepsPreviousIndex() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }
        let index = SearchIndex(path: path)

        try await index.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            files: files(10, sourceId: "vol:TEST")
        )
        try await index.stageFiles(files(400, sourceId: "vol:TEST"), sourceId: "vol:TEST")
        await index.discardStagedFiles(sourceId: "vol:TEST")

        #expect(await index.sourceCounts("vol:TEST")?.files == 10)

        // And the discarded rows are really gone — promoting now would
        // otherwise resurrect them.
        let promoted = try await index.promoteStagedFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            requested: 0, rejected: 0
        )
        #expect(promoted.stored == 0)
    }

    /// A quit in the middle of indexing leaves rows staged. They are not an
    /// index and must not become one on the next launch.
    @Test("Rows staged by a run that never finished are dropped at open")
    func staleStagingIsClearedOnOpen() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }

        let crashed = SearchIndex(path: path)
        try await crashed.stageFiles(files(50, sourceId: "vol:TEST"), sourceId: "vol:TEST")
        await crashed.close()

        let next = SearchIndex(path: path)
        let promoted = try await next.promoteStagedFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            requested: 0, rejected: 0
        )

        #expect(promoted.stored == 0)
        #expect(await next.sourceCounts("vol:TEST")?.files == 0)
    }

    /// Survives a quit: the point of an index is that it is still there next
    /// time, which is exactly what the reporter found it wasn't.
    @Test("An index written in one session is there in the next")
    func persistsAcrossSessions() async throws {
        let path = temporaryIndexPath()
        defer { try? FileManager.default.removeItem(atPath: path) }

        let first = SearchIndex(path: path)
        try await first.replaceFiles(
            sourceId: "vol:TEST", kind: "drive-network", displayName: "Share",
            files: files(42, sourceId: "vol:TEST")
        )
        await first.close()

        let second = SearchIndex(path: path)
        #expect(await second.sourceCounts("vol:TEST")?.files == 42)
        #expect(await second.listIndexedSources().first?.displayName == "Share")
    }
}
