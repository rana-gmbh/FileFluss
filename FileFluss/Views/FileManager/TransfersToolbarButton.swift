import SwiftUI
import FileFlussCore

/// Toolbar button that reports transfer activity and drops down the full
/// list, both panels at once.
///
/// Asked for in issue #47: transfer progress lived only in the destination
/// panel's sidebar, which is where it can be least visible — the sidebar may
/// be icon-only, or the Transfers section scrolled below a long list of
/// accounts. The toolbar is always in the same place, so activity is too.
struct TransfersToolbarButton: View {
    @Environment(AppState.self) private var appState
    @State private var showPopover = false

    var body: some View {
        let running = appState.runningTransfers
        Button {
            showPopover = true
        } label: {
            Label {
                LText("Transfers")
            } icon: {
                TransferActivityIcon(
                    fraction: appState.runningTransferFraction,
                    runningCount: running.count
                )
            }
        }
        .help(tooltip(running: running))
        .popover(isPresented: $showPopover, arrowEdge: .bottom) {
            TransfersPopover()
                .environment(appState)
        }
    }

    private func tooltip(running: [TransferProgress]) -> String {
        if running.isEmpty {
            return appState.hasFinishedTransfers
                ? L10n.text("Finished transfers")
                : L10n.text("No transfers")
        }
        let percent = Int(((appState.runningTransferFraction ?? 0) * 100).rounded())
        if running.count == 1 {
            return L10n.format("1 transfer — %d%%", percent)
        }
        return L10n.format("%d transfers — %d%%", running.count, percent)
    }
}

/// The button's icon: arrows inside a ring that fills with overall progress
/// while anything is running. The ring is what makes activity readable at
/// toolbar size — a percentage would be illegible at 16pt, and the number of
/// transfers is one click away in the popover.
private struct TransferActivityIcon: View {
    let fraction: Double?
    let runningCount: Int

    var body: some View {
        ZStack {
            if let fraction {
                Circle()
                    .stroke(Color.secondary.opacity(0.25), lineWidth: 1.5)
                Circle()
                    // A sliver even at zero, so a transfer that has only just
                    // started still looks like it started.
                    .trim(from: 0, to: max(0.03, fraction))
                    .stroke(
                        Color.accentColor,
                        style: StrokeStyle(lineWidth: 1.5, lineCap: .round)
                    )
                    .rotationEffect(.degrees(-90))
                    .animation(.easeOut(duration: 0.2), value: fraction)
            } else {
                Circle()
                    .stroke(Color.secondary.opacity(0.4), lineWidth: 1.2)
            }

            Image(systemName: "arrow.up.arrow.down")
                .font(.system(size: 8, weight: .bold))
        }
        .frame(width: 16, height: 16)
        .accessibilityLabel(
            runningCount > 0
                ? L10n.format("%d transfers running", runningCount)
                : L10n.text("No transfers")
        )
    }
}

/// The drop-down list. Rows are the same `TransferRow` the sidebar uses, so
/// progress, cancel and details behave identically wherever they're shown.
private struct TransfersPopover: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        let entries = appState.allTransfers
        // Which panel a transfer belongs to only matters when both have
        // some; otherwise the label is noise on every row.
        let showsPanel = !appState.leftTransfers.isEmpty && !appState.rightTransfers.isEmpty

        VStack(alignment: .leading, spacing: 0) {
            HStack {
                LText("Transfers")
                    .font(.headline)
                Spacer()
                if appState.hasFinishedTransfers {
                    Button(L10n.text("Clear Finished")) {
                        appState.clearFinishedTransfers()
                    }
                    .buttonStyle(.plain)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help(L10n.text("Remove transfers that have finished. Running transfers are left alone."))
                }
            }
            .padding(.horizontal, 12)
            .padding(.top, 10)
            .padding(.bottom, 6)

            if entries.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    LText("Nothing is being transferred.")
                        .font(.callout)
                    LText("Copies, moves, uploads and downloads show up here.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .padding(.horizontal, 12)
                .padding(.bottom, 12)
            } else {
                ScrollView {
                    VStack(spacing: 10) {
                        ForEach(entries) { entry in
                            VStack(alignment: .leading, spacing: 2) {
                                if showsPanel {
                                    Text(entry.panel == .left
                                         ? L10n.text("Left panel")
                                         : L10n.text("Right panel"))
                                        .font(.caption2)
                                        .foregroundStyle(.tertiary)
                                }
                                TransferRow(transfer: entry.transfer, panelSide: entry.panel)
                            }
                        }
                    }
                    .padding(.horizontal, 12)
                    .padding(.bottom, 12)
                }
                .frame(maxHeight: 420)
            }
        }
        .frame(width: 360)
    }
}
