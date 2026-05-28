import SwiftUI
import AppKit

/// Custom About panel with version, links, credits, and a manual
/// "Check for Updates" button driven by `UpdateChecker`.
struct AboutView: View {
    @StateObject private var checker = UpdateChecker()

    private var version: String {
        UpdateLookup.currentVersion()
    }

    private var releaseNotesURL: URL {
        URL(string: "https://github.com/rana-gmbh/filefluss/releases/tag/v\(version)")!
    }

    var body: some View {
        VStack(spacing: 0) {

            VStack(spacing: 8) {
                if let icon = NSApp.applicationIconImage {
                    Image(nsImage: icon)
                        .resizable()
                        .frame(width: 72, height: 72)
                }
                Text("FileFluss")
                    .font(.title2.bold())
                HStack(spacing: 6) {
                    Text("Version \(version)")
                        .foregroundStyle(.secondary)
                    Button("Release Notes ↗") {
                        NSWorkspace.shared.open(releaseNotesURL)
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(Color.accentColor)
                    .font(.caption)
                }
            }
            .padding(.top, 24)
            .padding(.bottom, 16)

            Divider()

            VStack(spacing: 4) {
                Text("Made by Rana GmbH")
                    .font(.callout)
                Button("www.filefluss.de") {
                    NSWorkspace.shared.open(URL(string: "https://www.filefluss.de")!)
                }
                .buttonStyle(.borderless)
                .foregroundStyle(Color.accentColor)
                .font(.callout)
                Button("github.com/rana-gmbh/filefluss") {
                    NSWorkspace.shared.open(URL(string: "https://github.com/rana-gmbh/filefluss")!)
                }
                .buttonStyle(.borderless)
                .foregroundStyle(Color.accentColor)
                .font(.callout)
            }
            .padding(.vertical, 16)

            Divider()

            VStack(spacing: 4) {
                Text("If you want to support this project,")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack(spacing: 0) {
                    Text("please consider to ")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Button("Buy me a coffee ↗") {
                        NSWorkspace.shared.open(URL(string: "https://buymeacoffee.com/robertrudolph")!)
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(Color.accentColor)
                    .font(.caption)
                }
            }
            .padding(.vertical, 12)

            Divider()

            VStack(spacing: 4) {
                Text("Released under the")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Button("GNU General Public License v3.0 ↗") {
                    NSWorkspace.shared.open(URL(string: "https://www.gnu.org/licenses/gpl-3.0.html")!)
                }
                .buttonStyle(.borderless)
                .foregroundStyle(Color.accentColor)
                .font(.caption)
                Text("App icon by @JohnnyFireOne · file-type icons by redbooth/free-file-icons (MIT)")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 16)
                    .padding(.top, 4)
            }
            .padding(.vertical, 12)

            Divider()

            updateSection
                .padding(.horizontal, 20)
                .padding(.vertical, 16)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(width: 320, height: 560)
    }

    @ViewBuilder
    private var updateSection: some View {
        switch checker.state {
        case .idle:
            Button("Check for Updates") {
                checker.check()
            }
            .buttonStyle(.borderedProminent)

        case .checking:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Checking for updates…")
                    .foregroundStyle(.secondary)
                    .font(.callout)
            }

        case .upToDate:
            VStack(spacing: 8) {
                Label("You're up to date", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green)
                    .font(.callout)
                Button("Check Again") {
                    checker.check()
                }
                .buttonStyle(.borderless)
                .foregroundStyle(.secondary)
                .font(.caption)
            }

        case .available(let update):
            VStack(alignment: .leading, spacing: 10) {
                Label("FileFluss \(update.version) is available!", systemImage: "arrow.down.circle.fill")
                    .foregroundStyle(Color.accentColor)
                    .font(.callout.bold())
                    .frame(maxWidth: .infinity, alignment: .center)

                if !update.releaseNotes.isEmpty {
                    ScrollView {
                        Group {
                            if let attr = try? AttributedString(
                                markdown: update.releaseNotes,
                                options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)
                            ) {
                                Text(attr)
                            } else {
                                Text(update.releaseNotes)
                            }
                        }
                        .font(.caption)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(2)
                    }
                    .frame(height: 90)
                    .padding(8)
                    .background(.quinary, in: RoundedRectangle(cornerRadius: 6))
                }

                HStack {
                    Button("Release Page ↗") {
                        NSWorkspace.shared.open(update.releasePageURL)
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .font(.caption)

                    Spacer()

                    Button("Download") {
                        NSWorkspace.shared.open(update.downloadURL ?? update.releasePageURL)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.small)
                }
            }

        case .failed(let message):
            VStack(spacing: 8) {
                Label("Could not check for updates", systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                    .font(.callout)
                Text(message)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                Button("Try Again") {
                    checker.check()
                }
                .buttonStyle(.borderless)
                .font(.caption)
            }
        }
    }
}
