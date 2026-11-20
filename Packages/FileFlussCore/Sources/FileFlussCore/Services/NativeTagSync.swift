import Foundation

/// What a cloud service can hold of an item's tags itself.
public enum NativeTagSupport: Sendable, Equatable {
    /// Nothing: the tags are FileFluss's own record only.
    case none
    /// One colour label per item and no names (MEGA).
    case oneColor
    /// Any number of named, coloured labels per item (kDrive's categories).
    case full
}

/// Keeps FileFluss's record of an item's tags and the service's own labels
/// in step, for the services that have labels.
///
/// Both sides can change: a tag is given in FileFluss, or a label in the
/// service's own app. When a folder is listed the two are compared, and the
/// rule is short. The service wins — what its own apps show is what the user
/// last did there — except for an item FileFluss has only just written
/// (an upload, a copy from another account), whose tags the service can't
/// know yet: those are sent to it.
public enum NativeTagSync {
    public struct Outcome: Equatable, Sendable {
        /// The item's tags after comparing the two sides.
        public let tags: [FileTag]
        /// What to write to the service, or nil when it already agrees.
        public let push: [FileTag]?
    }

    /// What the service is asked to hold for these tags: everything where
    /// it can, otherwise the last colour — the one a folder is shown in.
    public static func nativeForm(_ tags: [FileTag], support: NativeTagSupport) -> [FileTag] {
        switch support {
        case .none:
            return []
        case .oneColor:
            guard let last = tags.last(where: { $0.color != .none }) else { return [] }
            return [last]
        case .full:
            return tags
        }
    }

    /// - Parameters:
    ///   - stored: FileFluss's record for the item.
    ///   - native: the labels the service has for it.
    ///   - pending: the record was written by a transfer and the service
    ///     hasn't been told yet.
    ///   - standard: the seven colour tags under the names in use here, for
    ///     a colour that arrives from the service without a name.
    public static func reconcile(
        stored: [FileTag],
        native: [FileTag],
        support: NativeTagSupport,
        pending: Bool,
        standard: [FileTag]
    ) -> Outcome {
        switch support {
        case .none:
            return Outcome(tags: stored, push: nil)

        case .oneColor:
            let nativeColor = native.last(where: { $0.color != .none })?.color
            if stored.folderTint == nativeColor {
                return Outcome(tags: stored, push: nil)
            }
            if pending {
                return Outcome(tags: stored, push: nativeForm(stored, support: support))
            }
            guard let nativeColor else {
                // The label was taken off elsewhere: the colours go, the
                // tags without one stay.
                return Outcome(tags: stored.filter { $0.color == .none }, push: nil)
            }
            // Another colour was chosen elsewhere. It becomes the last tag,
            // under the name it already has here if it has one.
            var tags = stored
            let tag: FileTag
            if let index = tags.lastIndex(where: { $0.color == nativeColor }) {
                tag = tags.remove(at: index)
            } else {
                tag = standard.first(where: { $0.color == nativeColor })
                    ?? FileTag(name: nativeColor.englishName, color: nativeColor)
                tags.removeAll { $0.name == tag.name }
            }
            return Outcome(tags: tags + [tag], push: nil)

        case .full:
            guard pending else {
                return Outcome(tags: native, push: nil)
            }
            let known = Set(native.map { $0.name.lowercased() })
            let merged = native + stored.filter { !known.contains($0.name.lowercased()) }
            return Outcome(tags: merged, push: merged == native ? nil : merged)
        }
    }

    /// The tag colour nearest to an arbitrary one, for a service whose
    /// labels can be any colour (`#RRGGBB`). Unreadable colours are grey.
    public static func nearestColor(toHex hex: String) -> TagColor {
        guard let wanted = rgb(fromHex: hex) else { return .gray }
        var best = TagColor.gray
        var bestDistance = Int.max
        for color in TagColor.menuOrder {
            guard let candidate = rgb(fromHex: hexColor(for: color)) else { continue }
            let dr = wanted.r - candidate.r, dg = wanted.g - candidate.g, db = wanted.b - candidate.b
            let distance = dr * dr + dg * dg + db * db
            if distance < bestDistance {
                bestDistance = distance
                best = color
            }
        }
        return best
    }

    /// The colour FileFluss gives a label it creates on such a service.
    public static func hexColor(for color: TagColor) -> String {
        switch color {
        case .none, .gray: return "#8E8E93"
        case .green: return "#34C759"
        case .purple: return "#AF52DE"
        case .blue: return "#1E88FF"
        case .yellow: return "#FFCC00"
        case .red: return "#FF453A"
        case .orange: return "#FF9500"
        }
    }

    private static func rgb(fromHex hex: String) -> (r: Int, g: Int, b: Int)? {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        if text.count == 3 { text = text.map { "\($0)\($0)" }.joined() }
        guard text.count == 6, let value = Int(text, radix: 16) else { return nil }
        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)
    }
}
