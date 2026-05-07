import Foundation

/// Outcome of comparing one entry across the two folders.
enum FolderCompareStatus: Hashable, Sendable {
    case identical          // present on both sides, look the same
    case onlyLeft           // present only on the left side
    case onlyRight          // present only on the right side
    case differs            // present on both, but size or mtime differ
}

struct FolderCompareEntry: Identifiable, Hashable, Sendable {
    let relativePath: String
    let isDirectory: Bool
    let status: FolderCompareStatus
    let leftSize: Int64?
    let rightSize: Int64?
    let leftDate: Date?       // modification date
    let rightDate: Date?
    let leftCreated: Date?
    let rightCreated: Date?
    /// True when the difference is purely a date mismatch (same size).
    /// Used to label rows when Compare Date is enabled.
    let dateDiffersOnly: Bool

    var id: String { relativePath }
    var name: String { (relativePath as NSString).lastPathComponent }
}

struct FolderComparisonResult: Sendable {
    let entries: [FolderCompareEntry]

    var identicalCount: Int { entries.lazy.filter { $0.status == .identical }.count }
    var onlyLeftCount: Int { entries.lazy.filter { $0.status == .onlyLeft }.count }
    var onlyRightCount: Int { entries.lazy.filter { $0.status == .onlyRight }.count }
    var differsCount: Int { entries.lazy.filter { $0.status == .differs }.count }
}

enum FolderComparison {
    /// Cloud APIs and FAT filesystems quantize timestamps; treat anything
    /// within this window as equal. Matches SyncPlanner's tolerance.
    private static let modDateTolerance: TimeInterval = 2.0

    /// Compare two enumerated trees.
    /// - Parameter compareDates: when true, files with the same size but
    ///   diverging modification dates are flagged as different. When false
    ///   (default), only size differences count.
    static func compare(left: [SyncEntry], right: [SyncEntry], compareDates: Bool = false) -> FolderComparisonResult {
        var leftMap: [String: SyncEntry] = [:]
        leftMap.reserveCapacity(left.count)
        for e in left { leftMap[e.relativePath] = e }
        var rightMap: [String: SyncEntry] = [:]
        rightMap.reserveCapacity(right.count)
        for e in right { rightMap[e.relativePath] = e }

        var entries: [FolderCompareEntry] = []
        entries.reserveCapacity(max(leftMap.count, rightMap.count))
        let allPaths = Set(leftMap.keys).union(rightMap.keys)

        for path in allPaths.sorted() {
            switch (leftMap[path], rightMap[path]) {
            case (.some(let a), nil):
                entries.append(FolderCompareEntry(
                    relativePath: path, isDirectory: a.isDirectory, status: .onlyLeft,
                    leftSize: a.size, rightSize: nil,
                    leftDate: a.modificationDate, rightDate: nil,
                    leftCreated: a.creationDate, rightCreated: nil,
                    dateDiffersOnly: false
                ))
            case (nil, .some(let b)):
                entries.append(FolderCompareEntry(
                    relativePath: path, isDirectory: b.isDirectory, status: .onlyRight,
                    leftSize: nil, rightSize: b.size,
                    leftDate: nil, rightDate: b.modificationDate,
                    leftCreated: nil, rightCreated: b.creationDate,
                    dateDiffersOnly: false
                ))
            case (.some(let a), .some(let b)):
                let isDir = a.isDirectory
                let sizesMatch = a.size == b.size
                let datesMatch = abs(a.modificationDate.timeIntervalSince(b.modificationDate)) <= modDateTolerance
                let status: FolderCompareStatus
                var dateOnly = false
                if isDir {
                    status = .identical
                } else if !sizesMatch {
                    status = .differs
                } else if compareDates && !datesMatch {
                    status = .differs
                    dateOnly = true
                } else {
                    status = .identical
                }
                entries.append(FolderCompareEntry(
                    relativePath: path, isDirectory: isDir, status: status,
                    leftSize: a.size, rightSize: b.size,
                    leftDate: a.modificationDate, rightDate: b.modificationDate,
                    leftCreated: a.creationDate, rightCreated: b.creationDate,
                    dateDiffersOnly: dateOnly
                ))
            case (nil, nil):
                continue
            }
        }

        return FolderComparisonResult(entries: entries)
    }
}
