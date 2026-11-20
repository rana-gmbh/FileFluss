import Foundation

/// The colour of a tag. The raw values are the indexes Finder stores, so a
/// tag read from a file and one kept by FileFluss for a cloud account mean
/// the same thing.
public enum TagColor: Int, CaseIterable, Sendable, Codable, Hashable {
    case none = 0
    case gray = 1
    case green = 2
    case purple = 3
    case blue = 4
    case yellow = 5
    case red = 6
    case orange = 7

    /// The seven colours in the order Finder offers them.
    public static let menuOrder: [TagColor] = [.red, .orange, .yellow, .green, .blue, .purple, .gray]

    /// Used only where the system has no name to give — Finder's own names
    /// are localised and can be renamed by the user, so the app asks the
    /// system for them (see `SystemTagNames`).
    public var englishName: String {
        switch self {
        case .none: return ""
        case .gray: return "Gray"
        case .green: return "Green"
        case .purple: return "Purple"
        case .blue: return "Blue"
        case .yellow: return "Yellow"
        case .red: return "Red"
        case .orange: return "Orange"
        }
    }
}

/// One tag on a file or folder: a name and, usually, a colour.
///
/// The name is what identifies a tag, as in Finder: a file has "Red" or it
/// hasn't, and two tags of the same colour with different names are two
/// tags.
public struct FileTag: Hashable, Sendable, Codable {
    public let name: String
    public let color: TagColor

    public init(name: String, color: TagColor) {
        self.name = name
        self.color = color
    }
}

extension Array where Element == FileTag {
    /// The colours to draw as dots, in the order the tags were given.
    /// Tags without a colour have no dot.
    public var dotColors: [TagColor] {
        compactMap { $0.color == .none ? nil : $0.color }
    }

    /// The colour a folder takes: that of its last coloured tag, which is
    /// what Finder does on macOS 26 and later.
    public var folderTint: TagColor? {
        dotColors.last
    }
}

/// A change the Tags menu makes to an item's tags.
public enum TagChange: Hashable, Sendable {
    case add(FileTag)
    case remove(name: String)
    case removeAll

    /// The tags an item has after the change. Adding puts the tag last, so
    /// the newest colour is the one a folder shows.
    public func applied(to tags: [FileTag]) -> [FileTag] {
        switch self {
        case .add(let tag):
            guard !tags.contains(where: { $0.name == tag.name }) else { return tags }
            return tags + [tag]
        case .remove(let name):
            return tags.filter { $0.name != name }
        case .removeAll:
            return []
        }
    }
}

/// What the Tags menu shows for a selection: every tag on offer, and
/// whether all, some or none of the selected items have it.
public enum TagMenuModel {
    public enum Presence: Sendable, Equatable {
        case all, some, none
    }

    public struct Row: Sendable, Equatable {
        public let tag: FileTag
        public let presence: Presence

        /// What choosing the row does: a tag every item has is taken away,
        /// anything else is given to the items still missing it.
        public var change: TagChange {
            presence == .all ? .remove(name: tag.name) : .add(tag)
        }
    }

    /// The standard tags first, in the order given, then any other tags the
    /// selection carries — tags made in Finder, which can be taken off here
    /// but not created.
    public static func rows(standard: [FileTag], selection: [[FileTag]]) -> [Row] {
        var others: [FileTag] = []
        let standardNames = Set(standard.map(\.name))
        var seen = standardNames
        for tags in selection {
            for tag in tags where !seen.contains(tag.name) {
                seen.insert(tag.name)
                others.append(tag)
            }
        }
        return (standard + others).map { tag in
            let count = selection.reduce(0) { $0 + ($1.contains(where: { $0.name == tag.name }) ? 1 : 0) }
            let presence: Presence = count == 0 ? .none : (count == selection.count ? .all : .some)
            return Row(tag: tag, presence: presence)
        }
    }

    /// Whether there is anything for "Remove All Tags" to remove.
    public static func hasAnyTag(_ selection: [[FileTag]]) -> Bool {
        selection.contains { !$0.isEmpty }
    }
}
