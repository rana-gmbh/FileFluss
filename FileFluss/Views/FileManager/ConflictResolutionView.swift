import SwiftUI
import UniformTypeIdentifiers

// MARK: - Shared Conflict Types

struct ConflictFileInfo: Sendable {
    let name: String
    let date: Date
    let size: Int64
    let fileExtension: String
    let localURL: URL?  // non-nil for local files → used for file icon
}

enum ConflictChoice: Sendable {
    case replace, skip, keepBoth, stop
}

struct ConflictResolution: Sendable {
    let choice: ConflictChoice
    let applyToAll: Bool
}

enum ConflictDirection: Sendable {
    case leftToRight, rightToLeft
}

struct PendingConflict: Sendable {
    let source: ConflictFileInfo
    let destination: ConflictFileInfo
    let remainingConflicts: Int
    var direction: ConflictDirection = .leftToRight
}

// MARK: - View

struct ConflictResolutionView: View {
    let conflict: PendingConflict
    let onResolve: (ConflictResolution) -> Void

    @State private var applyToAll = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            fileComparison
                .padding(20)
            Divider()
            actionBar
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
        }
        .frame(width: 520)
        .background(.regularMaterial)
    }

    // MARK: - Header

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.title2)
                .foregroundStyle(.yellow)
            Text("An item named \"\(conflict.source.name)\" already exists in this location. What would you like to do?")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.yellow.opacity(0.12))
    }

    // MARK: - File Comparison

    private var leftInfo: ConflictFileInfo {
        conflict.direction == .leftToRight ? conflict.source : conflict.destination
    }

    private var rightInfo: ConflictFileInfo {
        conflict.direction == .leftToRight ? conflict.destination : conflict.source
    }

    private var fileComparison: some View {
        HStack(alignment: .top, spacing: 0) {
            fileColumn(
                info: leftInfo,
                ageLabel: ageLabel(for: leftInfo.date, against: rightInfo.date),
                isNewer: leftInfo.date > rightInfo.date
            )

            Spacer(minLength: 8)

            VStack {
                Spacer()
                Image(systemName: conflict.direction == .leftToRight
                      ? "arrowshape.right.fill"
                      : "arrowshape.left.fill")
                    .font(.title2)
                    .foregroundStyle(.tertiary)
                Spacer()
            }
            .frame(width: 40)

            Spacer(minLength: 8)

            fileColumn(
                info: rightInfo,
                ageLabel: ageLabel(for: rightInfo.date, against: leftInfo.date),
                isNewer: rightInfo.date > leftInfo.date
            )
        }
    }

    private func fileColumn(info: ConflictFileInfo, ageLabel: String, isNewer: Bool) -> some View {
        VStack(spacing: 6) {
            fileIcon(for: info)
                .frame(width: 64, height: 64)

            Text(info.name)
                .font(.caption)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: .infinity)

            Text(ageLabel)
                .font(.caption)
                .fontWeight(.semibold)
                .foregroundStyle(isNewer ? .green : .red)

            Text(formattedDate(info.date))
                .font(.caption2)
                .foregroundStyle(.secondary)

            Text(ByteCountFormatter.string(fromByteCount: info.size, countStyle: .file))
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
    }

    @ViewBuilder
    private func fileIcon(for info: ConflictFileInfo) -> some View {
        if let url = info.localURL {
            LocalFileIconView(url: url)
        } else {
            let utType = UTType(filenameExtension: info.fileExtension) ?? .data
            Image(nsImage: NSWorkspace.shared.icon(for: utType))
                .resizable()
                .aspectRatio(contentMode: .fit)
        }
    }

    private func ageLabel(for date: Date, against other: Date) -> String {
        if date > other {
            return "This file is newer."
        } else if date < other {
            return "This file is older."
        } else {
            return "Same date."
        }
    }

    private func formattedDate(_ date: Date) -> String {
        Self.dateFormatter.string(from: date)
    }

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    // MARK: - Action Bar

    private var actionBar: some View {
        HStack(spacing: 12) {
            actionButton(
                label: "Apply to all",
                icon: applyToAll ? "checkmark.circle.fill" : "circle",
                color: applyToAll ? .blue : .secondary,
                isToggle: true
            ) {
                applyToAll.toggle()
            }

            Spacer()

            actionButton(label: "Stop", icon: "xmark.circle.fill", color: .red) {
                onResolve(ConflictResolution(choice: .stop, applyToAll: applyToAll))
            }

            actionButton(label: "Skip", icon: "arrow.uturn.right.circle.fill", color: .secondary) {
                onResolve(ConflictResolution(choice: .skip, applyToAll: applyToAll))
            }

            actionButton(label: "Keep Both", icon: "doc.on.doc.fill", color: .secondary) {
                onResolve(ConflictResolution(choice: .keepBoth, applyToAll: applyToAll))
            }

            actionButton(label: "Replace", icon: "arrowshape.right.circle.fill", color: .blue) {
                onResolve(ConflictResolution(choice: .replace, applyToAll: applyToAll))
            }
        }
    }

    private func actionButton(label: String, icon: String, color: Color, isToggle: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(systemName: icon)
                    .font(.title2)
                    .foregroundStyle(color)
                Text(label)
                    .font(.caption2)
                    .foregroundStyle(.primary)
            }
            .frame(minWidth: 56)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Native File Icon (uses NSWorkspace for local files)

private struct LocalFileIconView: NSViewRepresentable {
    let url: URL

    func makeNSView(context: Context) -> NSImageView {
        let imageView = NSImageView()
        imageView.imageScaling = .scaleProportionallyUpOrDown
        imageView.image = NSWorkspace.shared.icon(forFile: url.path)
        return imageView
    }

    func updateNSView(_ nsView: NSImageView, context: Context) {
        nsView.image = NSWorkspace.shared.icon(forFile: url.path)
    }
}
