import AppKit
import FileFlussCore

// What the panels need to show tags and to change them: the colours, the
// dots beside a name, and the Tags submenu. Shared by the local and the
// cloud panel, list and gallery alike, so a tag looks and behaves the same
// wherever the item is kept.

extension TagColor {
    /// The system's colour for the tag, which follows light and dark mode.
    var nsColor: NSColor {
        switch self {
        case .none: return .clear
        case .gray: return .systemGray
        case .green: return .systemGreen
        case .purple: return .systemPurple
        case .blue: return .systemBlue
        case .yellow: return .systemYellow
        case .red: return .systemRed
        case .orange: return .systemOrange
        }
    }
}

/// The seven colour tags under the names this Mac gives them.
///
/// Finder's tag names are localised, and the user can rename them, so the
/// names come from the system rather than from FileFluss's own translations
/// — a tag set here then is the very tag Finder lists.
@MainActor
enum SystemTagNames {
    static var standard: [FileTag] {
        // Indexed by colour, with "None" first.
        let labels = NSWorkspace.shared.fileLabels
        return TagColor.menuOrder.map { color in
            let index = color.rawValue
            let name = index < labels.count && !labels[index].isEmpty ? labels[index] : color.englishName
            return FileTag(name: name, color: color)
        }
    }
}

// MARK: - Dots

/// The coloured dots beside a name, overlapping as in Finder. Shows the
/// last three; the newest is on the right, on top.
final class TagDotsView: NSView {
    static let diameter: CGFloat = 9
    static let step: CGFloat = 5
    static let maximum = 3

    var colors: [TagColor] = [] {
        didSet {
            guard colors != oldValue else { return }
            isHidden = colors.isEmpty
            invalidateIntrinsicContentSize()
            needsDisplay = true
        }
    }

    private var shown: ArraySlice<TagColor> { colors.suffix(Self.maximum) }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        isHidden = true
        setContentHuggingPriority(.required, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    static func width(for count: Int) -> CGFloat {
        let shown = min(count, maximum)
        return shown == 0 ? 0 : diameter + CGFloat(shown - 1) * step
    }

    override var intrinsicContentSize: NSSize {
        NSSize(width: Self.width(for: colors.count), height: Self.diameter)
    }

    override var isFlipped: Bool { true }

    /// A dot is not something to click: the click belongs to the row.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        let y = ((bounds.height - Self.diameter) / 2).rounded(.down)
        for (index, color) in shown.enumerated() {
            let rect = NSRect(x: CGFloat(index) * Self.step, y: y, width: Self.diameter, height: Self.diameter)
            if index > 0 {
                // A thin gap, so two dots of similar colour stay two dots.
                NSColor.textBackgroundColor.setFill()
                NSBezierPath(ovalIn: rect.insetBy(dx: -1, dy: -1)).fill()
            }
            color.nsColor.setFill()
            NSBezierPath(ovalIn: rect).fill()
        }
    }

    /// Adds a dots view to a name cell being built, after its name: the
    /// name ends where the dots begin, so a long one is shortened rather
    /// than drawn underneath them. Returns the constraints to activate.
    static func add(to cell: NSTableCellView, after textField: NSTextField) -> [NSLayoutConstraint] {
        let dots = TagDotsView()
        dots.translatesAutoresizingMaskIntoConstraints = false
        cell.addSubview(dots)
        return [
            dots.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -4),
            dots.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            dots.heightAnchor.constraint(equalToConstant: diameter),
            textField.trailingAnchor.constraint(equalTo: dots.leadingAnchor, constant: -4),
        ]
    }

    static func find(in cell: NSView) -> TagDotsView? {
        cell.subviews.first(where: { $0 is TagDotsView }) as? TagDotsView
    }
}

// MARK: - Menu

/// What a row of the Tags submenu does, and to which items.
final class TagMenuPayload: NSObject {
    let change: TagChange
    let itemIDs: [String]

    init(change: TagChange, itemIDs: [String]) {
        self.change = change
        self.itemIDs = itemIDs
    }
}

@MainActor
enum TagMenu {
    /// The "Tags" entry of a context menu, for the items it was opened on.
    /// Each row's `representedObject` is a `TagMenuPayload`.
    static func item(
        selection: [(id: String, tags: [FileTag])],
        target: AnyObject,
        action: Selector
    ) -> NSMenuItem {
        let parent = NSMenuItem(title: L10n.text("Tags"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        let standard = SystemTagNames.standard
        let tagSets = selection.map(\.tags)
        let ids = selection.map(\.id)

        for (index, row) in TagMenuModel.rows(standard: standard, selection: tagSets).enumerated() {
            if index == standard.count { submenu.addItem(.separator()) }
            let item = NSMenuItem(title: row.tag.name, action: action, keyEquivalent: "")
            item.target = target
            item.image = dotImage(row.tag.color)
            switch row.presence {
            case .all: item.state = .on
            case .some: item.state = .mixed
            case .none: item.state = .off
            }
            item.representedObject = TagMenuPayload(change: row.change, itemIDs: ids)
            submenu.addItem(item)
        }

        if TagMenuModel.hasAnyTag(tagSets) {
            submenu.addItem(.separator())
            let clear = NSMenuItem(title: L10n.text("Remove All Tags"), action: action, keyEquivalent: "")
            clear.target = target
            clear.representedObject = TagMenuPayload(change: .removeAll, itemIDs: ids)
            submenu.addItem(clear)
        }

        parent.submenu = submenu
        return parent
    }

    /// A filled dot in the tag's colour; a ring for a tag without one.
    static func dotImage(_ color: TagColor) -> NSImage {
        let side: CGFloat = 12
        return NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let circle = NSBezierPath(ovalIn: rect.insetBy(dx: 1.5, dy: 1.5))
            if color == .none {
                NSColor.secondaryLabelColor.setStroke()
                circle.lineWidth = 1
                circle.stroke()
            } else {
                color.nsColor.setFill()
                circle.fill()
            }
            return true
        }
    }
}
