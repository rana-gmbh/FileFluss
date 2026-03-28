import Testing
import Foundation
import UniformTypeIdentifiers
@testable import FileFluss

@Suite("FileItem Tests")
struct FileItemTests {

    @Test("File item from directory URL has correct properties")
    func directoryItem() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("TestDir_\(UUID())")
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: url) }

        let values = try url.resourceValues(forKeys: [
            .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
            .contentModificationDateKey, .creationDateKey, .isHiddenKey,
            .isSymbolicLinkKey, .contentTypeKey
        ])
        let item = FileItem(url: url, resourceValues: values)

        #expect(item.isDirectory)
        #expect(item.icon == "folder.fill")
        #expect(item.formattedSize == "--")
        #expect(item.name == url.lastPathComponent)
        #expect(item.id == url.path())
    }

    @Test("File item from regular file has correct properties")
    func regularFile() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("test_\(UUID()).txt")
        let data = Data("Hello FileFluss".utf8)
        try data.write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }

        let values = try url.resourceValues(forKeys: [
            .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
            .contentModificationDateKey, .creationDateKey, .isHiddenKey,
            .isSymbolicLinkKey, .contentTypeKey
        ])
        let item = FileItem(url: url, resourceValues: values)

        #expect(!item.isDirectory)
        #expect(item.size > 0)
        #expect(item.formattedSize != "--")
        #expect(!item.formattedDate.isEmpty)
    }

    @Test("Icon returns correct SF Symbol for content types")
    func iconMapping() throws {
        let cases: [(String, String)] = [
            ("photo.png", "photo"),
            ("video.mp4", "film"),
            ("song.mp3", "music.note"),
            ("doc.pdf", "doc.richtext"),
            ("code.swift", "chevron.left.forwardslash.chevron.right"),
            ("archive.zip", "doc.zipper"),
            ("readme.txt", "doc.text"),
        ]

        for (filename, expectedIcon) in cases {
            let url = FileManager.default.temporaryDirectory.appendingPathComponent("icon_test_\(UUID())_\(filename)")
            try Data().write(to: url)
            defer { try? FileManager.default.removeItem(at: url) }

            let values = try url.resourceValues(forKeys: [
                .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
                .contentModificationDateKey, .creationDateKey, .isHiddenKey,
                .isSymbolicLinkKey, .contentTypeKey
            ])
            let item = FileItem(url: url, resourceValues: values)
            #expect(item.icon == expectedIcon, "Expected \(expectedIcon) for \(filename), got \(item.icon)")
        }
    }
}
