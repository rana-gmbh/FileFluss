import SwiftUI
import FileFlussCore

/// The views that render a transfer's progress. Shared, not duplicated: the
/// same row appears in a panel sidebar's Transfers section, in that
/// section's popover when the sidebar is icon-only, and in the toolbar's
/// transfers popover, which shows both panels' transfers at once.

struct TransferRow: View {
    let transfer: TransferProgress
    let panelSide: PanelSide
    @Environment(AppState.self) private var appState
    @State private var showDetails = false
    @State private var showCancelConfirmation = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                if let details = transfer.completionDetailNames, transfer.isComplete {
                    Text(transfer.statusText)
                        .font(.caption)
                        .lineLimit(1)
                        .help(details)
                } else {
                    Text(transfer.statusText)
                        .font(.caption)
                        .lineLimit(1)
                }
                Spacer()
                if transfer.isComplete {
                    Button(L10n.text("Details")) {
                        showDetails = true
                    }
                    .font(.caption2)
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    Button {
                        appState.removeTransfer(id: transfer.id, panel: panelSide)
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.tertiary)
                    }
                    .buttonStyle(.plain)
                } else if !transfer.isCancelled {
                    Button {
                        showCancelConfirmation = true
                    } label: {
                        LText("Cancel")
                            .font(.caption2)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help(L10n.text("Cancel this transfer"))
                }
            }

            CapsuleProgressBar(transfer: transfer)
                .frame(height: 18)

            if !transfer.currentFileName.isEmpty && !transfer.isComplete {
                Text(transfer.currentFileName)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .popover(isPresented: $showDetails) {
            TransferDetailsView(transfer: transfer)
        }
        .confirmationDialog(
            "Cancel this transfer?",
            isPresented: $showCancelConfirmation,
            titleVisibility: .visible
        ) {
            Button(L10n.text("Cancel Transfer"), role: .destructive) {
                transfer.cancel()
            }
            Button(L10n.text("Keep Running"), role: .cancel) {}
        } message: {
            LText("Files already transferred will remain. Any partial file currently in flight will be discarded.")
        }
    }
}

// MARK: - Capsule Progress Bar

struct CapsuleProgressBar: View {
    let transfer: TransferProgress

    private var tintGradient: LinearGradient {
        let colors: [Color]
        if transfer.isComplete {
            if transfer.hasErrors {
                // Partial success gets orange, full failure gets red so the
                // user can tell at a glance whether anything got through.
                colors = transfer.successCount > 0
                    ? [Color.orange.opacity(0.85), Color.orange]
                    : [Color.red.opacity(0.85), Color.red]
            } else {
                colors = [Color.green.opacity(0.85), Color.green]
            }
        } else if transfer.isCloudToCloud {
            colors = transfer.currentPhase == .downloading
                ? [Color.blue.opacity(0.85), Color.cyan]
                : [Color.purple.opacity(0.85), Color.pink.opacity(0.9)]
        } else if transfer.isCloudUpload {
            colors = [Color.purple.opacity(0.85), Color.pink.opacity(0.9)]
        } else {
            colors = [Color.blue.opacity(0.85), Color.cyan]
        }
        return LinearGradient(colors: colors, startPoint: .leading, endPoint: .trailing)
    }

    var body: some View {
        GeometryReader { geo in
            let fraction = max(0, min(1, transfer.fraction))
            let filledWidth = geo.size.width * fraction

            ZStack(alignment: .leading) {
                // Track
                Capsule()
                    .fill(Color.primary.opacity(0.08))
                    .overlay(
                        Capsule()
                            .strokeBorder(Color.primary.opacity(0.05), lineWidth: 0.5)
                    )

                // Fill
                Capsule()
                    .fill(tintGradient)
                    .frame(width: filledWidth)
                    .animation(.easeOut(duration: 0.15), value: fraction)

                // Percentage label, centered in the bar
                Text(transfer.percentText)
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(fraction > 0.55 ? Color.white : Color.primary.opacity(0.75))
                    .frame(maxWidth: .infinity, alignment: .center)
                    .shadow(color: fraction > 0.55 ? .black.opacity(0.15) : .clear, radius: 0.5, y: 0.5)
            }
        }
    }
}

struct TransferDetailsView: View {
    let transfer: TransferProgress

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            LText("Transfer Details")
                .font(.headline)

            if let errorMessage = transfer.errorMessage {
                HStack(alignment: .top, spacing: 4) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                    Text(errorMessage)
                        .foregroundStyle(.red)
                        .font(.callout)
                        .textSelection(.enabled)
                }
                .padding(.vertical, 2)
            }

            if transfer.failureCount > 0 || transfer.successCount > 0 {
                HStack(spacing: 10) {
                    if transfer.successCount > 0 {
                        Label(L10n.format("%d succeeded", transfer.successCount), systemImage: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                    }
                    if transfer.failureCount > 0 {
                        Label(L10n.format("%d failed", transfer.failureCount), systemImage: "xmark.octagon.fill")
                            .foregroundStyle(.red)
                    }
                    if transfer.skippedCount > 0 {
                        Label(L10n.format("%d skipped", transfer.skippedCount), systemImage: "minus.circle.fill")
                            .foregroundStyle(.secondary)
                    }
                }
                .font(.caption)
            }

            Divider()

            LabeledContent("Operation") {
                Text(L10n.text(transfer.operation))
            }
            LabeledContent("Finished") {
                Text(transfer.formattedEndTime)
            }
            if transfer.totalBytes > 0 {
                LabeledContent("Total Size") {
                    Text(ByteCountFormatter.string(fromByteCount: transfer.totalBytes, countStyle: .file))
                }
                if transfer.isCloudToCloud {
                    LabeledContent("Download Speed") {
                        Text(transfer.downloadSpeed)
                    }
                    LabeledContent("Upload Speed") {
                        Text(transfer.uploadSpeed)
                    }
                } else if transfer.isCloudDownload {
                    LabeledContent("Download Speed") {
                        Text(transfer.averageSpeed)
                    }
                } else if transfer.isCloudUpload {
                    LabeledContent("Upload Speed") {
                        Text(transfer.averageSpeed)
                    }
                } else {
                    LabeledContent("Avg. Speed") {
                        Text(transfer.averageSpeed)
                    }
                }
            }

            Divider()

            itemsList
        }
        .padding()
        .frame(width: 340)
    }

    @ViewBuilder
    private var itemsList: some View {
        if !transfer.itemResults.isEmpty {
            Text(L10n.format("Items (%d)", transfer.itemResults.count))
                .font(.subheadline)
                .foregroundStyle(.secondary)

            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(transfer.itemResults) { result in
                        TransferItemRow(result: result)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 220)
        } else if !transfer.transferredFileNames.isEmpty {
            // Fallback for transfers that didn't record per-item results
            // (e.g. older code paths still in flight).
            Text(L10n.format("Items (%d)", transfer.transferredFileNames.count))
                .font(.subheadline)
                .foregroundStyle(.secondary)

            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(transfer.transferredFileNames, id: \.self) { name in
                        Text(name)
                            .font(.caption)
                            .lineLimit(1)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 150)
        }
    }
}

struct TransferItemRow: View {
    let result: TransferItemResult

    private var icon: (name: String, color: Color) {
        switch result.status {
        case .succeeded: return ("checkmark.circle.fill", .green)
        case .failed: return ("xmark.octagon.fill", .red)
        case .skipped: return ("minus.circle.fill", .secondary)
        case .cancelled: return ("slash.circle.fill", .secondary)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 6) {
                Image(systemName: icon.name)
                    .foregroundStyle(icon.color)
                    .font(.caption)
                Text(result.name)
                    .font(.caption)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(result.name)
            }
            if let error = result.errorMessage {
                Text(error)
                    .font(.caption2)
                    .foregroundStyle(.red)
                    .lineLimit(2)
                    .padding(.leading, 18)
                    .textSelection(.enabled)
            }
        }
    }
}
