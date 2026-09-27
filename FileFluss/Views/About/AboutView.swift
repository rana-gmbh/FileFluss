import SwiftUI
import AppKit
import FileFlussCore

/// Custom About panel with version, links, credits, and a manual
/// "Check for Updates" button driven by Sparkle.
struct AboutView: View {
    @ObservedObject private var updater = AppUpdater.shared

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
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
                    Text(L10n.format("Version %@", version))
                        .foregroundStyle(.secondary)
                    Button(action: {
                        NSWorkspace.shared.open(releaseNotesURL)
                    }) { LText("Release Notes ↗") }
                    .buttonStyle(.borderless)
                    .foregroundStyle(Color.accentColor)
                    .font(.caption)
                }
            }
            .padding(.top, 24)
            .padding(.bottom, 16)

            Divider()

            VStack(spacing: 4) {
                LText("Made by Rana GmbH")
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
                LText("If you want to support this project,")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack(spacing: 0) {
                    LText("please consider to ")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Button(action: {
                        NSWorkspace.shared.open(URL(string: "https://buymeacoffee.com/robertrudolph")!)
                    }) { LText("Buy me a coffee ↗") }
                    .buttonStyle(.borderless)
                    .foregroundStyle(Color.accentColor)
                    .font(.caption)
                }
            }
            .padding(.vertical, 12)

            Divider()

            VStack(spacing: 4) {
                LText("Released under the")
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
        VStack(spacing: 8) {
            Button {
                updater.checkForUpdates(nil)
            } label: {
                LText("Check for Updates")
            }
            .buttonStyle(.borderedProminent)
            // Sparkle disables checking while one is already running.
            .disabled(updater.isAvailable && !updater.canCheckForUpdates)

            if let last = updater.lastUpdateCheckDate {
                Text(L10n.format("Last checked: %@", last.formatted(date: .abbreviated, time: .shortened)))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else if !updater.isAvailable {
                // Unbundled dev runs and builds without the public key can't
                // verify an update, so the button opens the releases page
                // instead of pretending to check.
                LText("Opens the releases page in your browser.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }
}
