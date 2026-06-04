import SwiftUI
import AppKit
import FileFlussCore

/// A rename text field backed by `NSTextField` so it can pre-select only the
/// base name (everything before the file extension) the way Finder does —
/// SwiftUI's `TextField` always selects everything and offers no way to choose
/// a sub-range. Typing therefore replaces the name while keeping the extension.
struct RenameField: NSViewRepresentable {
    @Binding var text: String
    var onCommit: () -> Void
    var onCancel: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> RenameNSTextField {
        let field = RenameNSTextField(string: text)
        field.delegate = context.coordinator
        field.lineBreakMode = .byTruncatingTail
        field.usesSingleLineMode = true
        field.bezelStyle = .roundedBezel
        // Deferred so the field is in its window with a field editor available.
        DispatchQueue.main.async { field.focusAndSelectBaseName() }
        return field
    }

    func updateNSView(_ field: RenameNSTextField, context: Context) {
        if field.stringValue != text { field.stringValue = text }
        // Retry the initial selection if the field wasn't in a window yet at
        // make time (the call no-ops once it has succeeded).
        DispatchQueue.main.async { field.focusAndSelectBaseName() }
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        private let parent: RenameField
        init(_ parent: RenameField) { self.parent = parent }

        func controlTextDidChange(_ note: Notification) {
            guard let field = note.object as? NSTextField else { return }
            parent.text = field.stringValue
        }

        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            switch selector {
            case #selector(NSResponder.insertNewline(_:)):
                parent.onCommit()
                return true
            case #selector(NSResponder.cancelOperation(_:)):
                parent.onCancel()
                return true
            default:
                return false
            }
        }
    }
}

/// NSTextField that focuses itself and selects just the base name exactly once.
final class RenameNSTextField: NSTextField {
    private var didInitialSelect = false

    /// Becomes first responder and selects the name up to (but not including)
    /// the extension. The field editor — not the text field — owns the
    /// selection, so the range is set there. Runs at most once.
    func focusAndSelectBaseName() {
        guard !didInitialSelect, let window else { return }
        didInitialSelect = window.makeFirstResponder(self)
        guard didInitialSelect, let editor = currentEditor() else { return }
        let name = stringValue as NSString
        let ext = name.pathExtension
        // No extension (folder, or a dotfile like ".bashrc") → select all,
        // matching Finder. Otherwise drop the trailing ".ext".
        let length = ext.isEmpty ? name.length : name.length - (ext as NSString).length - 1
        editor.selectedRange = NSRange(location: 0, length: max(0, length))
    }
}

/// Compact rename dialog hosting a `RenameField`. Replaces the plain SwiftUI
/// alert so the base name can be pre-selected; Enter renames, Escape cancels
/// (handled by the field while it holds focus).
struct RenameSheet: View {
    @Binding var text: String
    let onRename: () -> Void
    let onCancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(L10n.text("Rename")).font(.headline)
            RenameField(text: $text, onCommit: onRename, onCancel: onCancel)
                .frame(width: 300)
            HStack {
                Spacer()
                Button(L10n.text("Cancel"), role: .cancel) { onCancel() }
                Button(L10n.text("Rename")) { onRename() }
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
    }
}
