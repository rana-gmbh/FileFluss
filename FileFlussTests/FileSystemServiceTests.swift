import Testing
import Foundation
@testable import FileFluss

@Suite("FileSystemService Tests")
struct FileSystemServiceTests {
    let service = FileSystemService.shared
    let testBaseDir = FileManager.default.temporaryDirectory.appendingPathComponent("FileFlussTests_\(UUID())")

    init() throws {
        try FileManager.default.createDirectory(at: testBaseDir, withIntermediateDirectories: true)
    }

    @Test("List directory returns files and folders")
    func listDirectory() async throws {
        let subdir = testBaseDir.appendingPathComponent("subfolder")
        try FileManager.default.createDirectory(at: subdir, withIntermediateDirectories: true)
        try Data("content".utf8).write(to: testBaseDir.appendingPathComponent("file.txt"))
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        let items = try await service.listDirectory(at: testBaseDir)

        #expect(items.count == 2)
        let names = Set(items.map(\.name))
        #expect(names.contains("subfolder"))
        #expect(names.contains("file.txt"))

        let dir = items.first { $0.name == "subfolder" }
        #expect(dir?.isDirectory == true)

        let file = items.first { $0.name == "file.txt" }
        #expect(file?.isDirectory == false)
        #expect(file!.size > 0)
    }

    @Test("List directory skips hidden files by default")
    func listDirectorySkipsHidden() async throws {
        try Data().write(to: testBaseDir.appendingPathComponent(".hidden"))
        try Data().write(to: testBaseDir.appendingPathComponent("visible.txt"))
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        let items = try await service.listDirectory(at: testBaseDir, showHidden: false)
        let names = items.map(\.name)
        #expect(!names.contains(".hidden"))
        #expect(names.contains("visible.txt"))
    }

    @Test("List directory shows hidden files when requested")
    func listDirectoryShowsHidden() async throws {
        try Data().write(to: testBaseDir.appendingPathComponent(".hidden"))
        try Data().write(to: testBaseDir.appendingPathComponent("visible.txt"))
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        let items = try await service.listDirectory(at: testBaseDir, showHidden: true)
        let names = items.map(\.name)
        #expect(names.contains(".hidden"))
        #expect(names.contains("visible.txt"))
    }

    @Test("Create directory")
    func createDirectory() async throws {
        let newDir = testBaseDir.appendingPathComponent("newFolder")
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        try await service.createDirectory(at: newDir)

        var isDir: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: newDir.path(), isDirectory: &isDir)
        #expect(exists)
        #expect(isDir.boolValue)
    }

    @Test("Delete item removes file")
    func deleteFile() async throws {
        let file = testBaseDir.appendingPathComponent("toDelete.txt")
        try Data("delete me".utf8).write(to: file)
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        #expect(FileManager.default.fileExists(atPath: file.path()))

        try await service.deleteItem(at: file)

        #expect(!FileManager.default.fileExists(atPath: file.path()))
    }

    @Test("Move item relocates file")
    func moveItem() async throws {
        let source = testBaseDir.appendingPathComponent("source.txt")
        let dest = testBaseDir.appendingPathComponent("dest.txt")
        try Data("move me".utf8).write(to: source)
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        try await service.moveItem(from: source, to: dest)

        #expect(!FileManager.default.fileExists(atPath: source.path()))
        #expect(FileManager.default.fileExists(atPath: dest.path()))
    }

    @Test("Copy item duplicates file")
    func copyItem() async throws {
        let source = testBaseDir.appendingPathComponent("original.txt")
        let dest = testBaseDir.appendingPathComponent("copy.txt")
        try Data("copy me".utf8).write(to: source)
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        try await service.copyItem(from: source, to: dest)

        #expect(FileManager.default.fileExists(atPath: source.path()))
        #expect(FileManager.default.fileExists(atPath: dest.path()))
        let content = try String(contentsOf: dest, encoding: .utf8)
        #expect(content == "copy me")
    }

    @Test("Item exists returns correct result")
    func itemExists() async throws {
        let file = testBaseDir.appendingPathComponent("exists.txt")
        try Data().write(to: file)
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        let exists = await service.itemExists(at: file)
        #expect(exists)

        let notExists = await service.itemExists(at: testBaseDir.appendingPathComponent("nope.txt"))
        #expect(!notExists)
    }

    @Test("List empty directory returns empty array")
    func listEmptyDirectory() async throws {
        defer { try? FileManager.default.removeItem(at: testBaseDir) }

        let items = try await service.listDirectory(at: testBaseDir)
        #expect(items.isEmpty)
    }

    @Test("List nonexistent directory throws error")
    func listNonexistent() async throws {
        let badURL = testBaseDir.appendingPathComponent("doesNotExist")

        await #expect(throws: (any Error).self) {
            try await service.listDirectory(at: badURL)
        }
    }
}
