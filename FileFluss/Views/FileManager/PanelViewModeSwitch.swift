import SwiftUI
import FileFlussCore

/// List / gallery switch at the trailing end of a panel's path bar. It
/// belongs to its panel rather than to the toolbar so it is obvious which
/// side it changes, and each side can show what suits its folder.
struct PanelViewModeSwitch: View {
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState

    var body: some View {
        Picker(selection: Binding(
            get: { appState.viewMode(for: panelSide) },
            set: { appState.setViewMode($0, for: panelSide) }
        )) {
            Label(L10n.text("List"), systemImage: "list.bullet")
                .tag(FileViewMode.list)
            Label(L10n.text("Gallery"), systemImage: "square.grid.2x2")
                .tag(FileViewMode.gallery)
        } label: {
            Text(L10n.text("View"))
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .labelStyle(.iconOnly)
        .controlSize(.small)
        .fixedSize()
        .help(L10n.text("Show this panel as a list or as a gallery"))
    }
}
