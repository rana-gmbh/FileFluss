import SwiftUI
import AppKit

/// Settings tab that lists every bindable command grouped by section.
/// Users can pick a preset (Finder / Commander), record a custom shortcut
/// per command, clear a binding, or reset everything back to the preset
/// defaults.
///
/// Visual style mirrors a stock macOS preferences pane (Snippety, Raycast,
/// etc.): right-aligned label, inline shortcut field with glyphs and small
/// reset/clear glyph buttons, "Record Shortcut" placeholder when empty.
struct KeyboardMapSettingsView: View {
    @State private var manager = KeyboardShortcutManager.shared
    @State private var recordingCommand: KeyboardCommand?
    @State private var resetConfirm = false

    private let labelWidth: CGFloat = 230
    private let fieldWidth: CGFloat = 180

    var body: some View {
        VStack(spacing: 0) {
            presetPicker
            Divider()
            commandList
            Divider()
            footer
        }
    }

    // MARK: - Sections

    private var presetPicker: some View {
        VStack(spacing: 6) {
            Picker("", selection: Bindable(manager).preset) {
                ForEach(KeyboardShortcutManager.Preset.allCases) { p in
                    Text(p.displayName).tag(p)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: 380)
            Text("Pick a preset, then customise individual commands below.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
    }

    private var commandList: some View {
        ScrollView {
            VStack(spacing: 18) {
                ForEach(KeyboardCommand.Section.allCases, id: \.self) { section in
                    let commands = KeyboardCommand.allCases.filter { $0.section == section }
                    if !commands.isEmpty {
                        sectionBlock(title: section.rawValue, commands: commands)
                    }
                }
            }
            .padding(.vertical, 16)
        }
    }

    private func sectionBlock(title: String, commands: [KeyboardCommand]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Spacer()
                Text(title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .frame(width: labelWidth + fieldWidth + 12, alignment: .leading)
            }
            ForEach(commands) { cmd in
                commandRow(cmd)
            }
        }
    }

    private func commandRow(_ command: KeyboardCommand) -> some View {
        HStack(spacing: 12) {
            Spacer(minLength: 0)
            Text("\(command.displayName):")
                .frame(width: labelWidth, alignment: .trailing)
                .lineLimit(1)
            shortcutField(for: command)
                .frame(width: fieldWidth)
            Spacer(minLength: 0)
        }
    }

    @ViewBuilder
    private func shortcutField(for command: KeyboardCommand) -> some View {
        let isRecording = recordingCommand == command
        let binding = manager.binding(for: command)
        let isCustomised = manager.isCustomised(command)

        if isRecording {
            recordingField()
                .background(
                    ShortcutRecorder { result in
                        if let result {
                            manager.setBinding(result, for: command)
                        }
                        recordingCommand = nil
                    }
                )
        } else if let binding {
            assignedField(
                binding: binding,
                showReset: isCustomised,
                onRecord: { recordingCommand = command },
                onReset: { manager.resetToPresetDefault(command) },
                onClear: { manager.setBinding(.init(key: "", modifiers: []), for: command) }
            )
        } else {
            Button {
                recordingCommand = command
            } label: {
                Text("Record Shortcut")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .controlSize(.regular)
        }
    }

    private func recordingField() -> some View {
        HStack {
            Spacer(minLength: 0)
            Text("Press a key combo…")
                .font(.callout)
                .foregroundStyle(Color.accentColor)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(Color.accentColor.opacity(0.08))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .strokeBorder(Color.accentColor, lineWidth: 1.5)
        )
    }

    private func assignedField(
        binding: ShortcutBinding,
        showReset: Bool,
        onRecord: @escaping () -> Void,
        onReset: @escaping () -> Void,
        onClear: @escaping () -> Void
    ) -> some View {
        HStack(spacing: 6) {
            Button(action: onRecord) {
                Text(binding.displayString)
                    .font(.system(.callout, design: .default).weight(.medium))
                    .frame(maxWidth: .infinity, alignment: .center)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Click to record a new shortcut")

            if showReset {
                Button(action: onReset) {
                    Image(systemName: "arrow.uturn.backward")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Reset to preset default")
            }
            Button(action: onClear) {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Clear shortcut")
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(Color(nsColor: .textBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .strokeBorder(Color.secondary.opacity(0.25), lineWidth: 1)
        )
    }

    private var footer: some View {
        HStack {
            Text("Click a shortcut to record. Esc cancels.")
                .font(.caption)
                .foregroundStyle(.secondary)
            Spacer()
            Button("Reset All to Preset Defaults", role: .destructive) {
                resetConfirm = true
            }
            .disabled(manager.customBindings.isEmpty)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .confirmationDialog(
            "Reset every customised shortcut?",
            isPresented: $resetConfirm,
            titleVisibility: .visible
        ) {
            Button("Reset", role: .destructive) {
                manager.resetAllToPresetDefaults()
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("All shortcuts will revert to the \(manager.preset.displayName) preset.")
        }
    }
}

/// Invisible NSView wrapper that captures the next key-down and reports
/// it back as a `ShortcutBinding`. Pressing Escape (or clicking elsewhere)
/// aborts with `nil`.
private struct ShortcutRecorder: NSViewRepresentable {
    let onCapture: (ShortcutBinding?) -> Void

    func makeNSView(context: Context) -> KeyCatcherView {
        let view = KeyCatcherView()
        view.onCapture = onCapture
        DispatchQueue.main.async { view.window?.makeFirstResponder(view) }
        return view
    }

    func updateNSView(_ nsView: KeyCatcherView, context: Context) {
        nsView.onCapture = onCapture
        DispatchQueue.main.async { nsView.window?.makeFirstResponder(nsView) }
    }

    final class KeyCatcherView: NSView {
        var onCapture: ((ShortcutBinding?) -> Void)?

        override var acceptsFirstResponder: Bool { true }

        override func keyDown(with event: NSEvent) {
            if event.keyCode == 53 { // Escape
                onCapture?(nil)
                return
            }
            guard let binding = Self.binding(from: event) else { return }
            onCapture?(binding)
        }

        override func performKeyEquivalent(with event: NSEvent) -> Bool {
            if let binding = Self.binding(from: event) {
                onCapture?(binding)
                return true
            }
            return super.performKeyEquivalent(with: event)
        }

        private static func binding(from event: NSEvent) -> ShortcutBinding? {
            var mods: ShortcutBinding.Modifiers = []
            if event.modifierFlags.contains(.command) { mods.insert(.command) }
            if event.modifierFlags.contains(.shift)   { mods.insert(.shift) }
            if event.modifierFlags.contains(.option)  { mods.insert(.option) }
            if event.modifierFlags.contains(.control) { mods.insert(.control) }

            switch event.keyCode {
            case 36:  return ShortcutBinding(key: "return", modifiers: mods)
            case 48:  return ShortcutBinding(key: "tab",    modifiers: mods)
            case 49:  return ShortcutBinding(key: "space",  modifiers: mods)
            case 51:  return ShortcutBinding(key: "delete", modifiers: mods)
            case 117: return ShortcutBinding(key: "fwdDelete", modifiers: mods)
            case 53:  return nil
            case 123: return ShortcutBinding(key: "left",   modifiers: mods)
            case 124: return ShortcutBinding(key: "right",  modifiers: mods)
            case 125: return ShortcutBinding(key: "down",   modifiers: mods)
            case 126: return ShortcutBinding(key: "up",     modifiers: mods)
            case 115: return ShortcutBinding(key: "home",   modifiers: mods)
            case 119: return ShortcutBinding(key: "end",    modifiers: mods)
            case 116: return ShortcutBinding(key: "pgUp",   modifiers: mods)
            case 121: return ShortcutBinding(key: "pgDown", modifiers: mods)
            case 122: return ShortcutBinding(key: "f1",  modifiers: mods)
            case 120: return ShortcutBinding(key: "f2",  modifiers: mods)
            case 99:  return ShortcutBinding(key: "f3",  modifiers: mods)
            case 118: return ShortcutBinding(key: "f4",  modifiers: mods)
            case 96:  return ShortcutBinding(key: "f5",  modifiers: mods)
            case 97:  return ShortcutBinding(key: "f6",  modifiers: mods)
            case 98:  return ShortcutBinding(key: "f7",  modifiers: mods)
            case 100: return ShortcutBinding(key: "f8",  modifiers: mods)
            case 101: return ShortcutBinding(key: "f9",  modifiers: mods)
            case 109: return ShortcutBinding(key: "f10", modifiers: mods)
            case 103: return ShortcutBinding(key: "f11", modifiers: mods)
            case 111: return ShortcutBinding(key: "f12", modifiers: mods)
            default: break
            }

            guard let chars = event.charactersIgnoringModifiers?.lowercased(),
                  let c = chars.first else { return nil }
            switch c {
            case ".": return ShortcutBinding(key: "period", modifiers: mods)
            case ",": return ShortcutBinding(key: "comma", modifiers: mods)
            case "+": return ShortcutBinding(key: "plus", modifiers: mods)
            case "-": return ShortcutBinding(key: "minus", modifiers: mods)
            case "=": return ShortcutBinding(key: "equals", modifiers: mods)
            case "/": return ShortcutBinding(key: "slash", modifiers: mods)
            case "\\": return ShortcutBinding(key: "backslash", modifiers: mods)
            default: return ShortcutBinding(key: String(c), modifiers: mods)
            }
        }
    }
}
