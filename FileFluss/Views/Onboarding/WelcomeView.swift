import SwiftUI
import AppKit
import FileFlussCore

/// First-launch onboarding. Surfaces the Full Disk Access option because
/// without it macOS prompts for each protected folder (Desktop, Documents,
/// Downloads, iCloud Drive, …) the first time it's opened, and the prompts
/// can take several seconds to clear — which feels like a hang.
struct WelcomeView: View {
    @AppStorage("hasCompletedWelcome") private var hasCompletedWelcome = false
    @Environment(\.dismiss) private var dismiss

    @State private var didOpenSettings = false

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(spacing: 20) {
                    if !didOpenSettings {
                        introContent
                    } else {
                        dragInstructionsContent
                    }
                }
                .padding(.horizontal, 24)
                .padding(.top, 28)
                .padding(.bottom, 20)
            }

            Divider()

            HStack {
                Button(action: {
                    finish()
                }) { LText("Continue without granting Full Disk Access") }
                .keyboardShortcut(.cancelAction)

                Spacer()

                if didOpenSettings {
                    Button(action: {
                        finish()
                    }) { LText("Done, Don't Relaunch") }

                    Button {
                        quitAndRelaunch()
                    } label: {
                        Label(L10n.text("Quit & Relaunch"), systemImage: "arrow.clockwise")
                    }
                    .keyboardShortcut(.defaultAction)
                } else {
                    Button {
                        openFullDiskAccessSettings()
                        didOpenSettings = true
                    } label: {
                        Label(L10n.text("Open System Settings…"), systemImage: "arrow.up.right.square")
                    }
                    .keyboardShortcut(.defaultAction)
                }
            }
            .padding(16)
        }
        .frame(width: 560, height: 480)
    }

    // MARK: - Intro

    private var introContent: some View {
        VStack(spacing: 20) {
            Image(systemName: "externaldrive.connected.to.line.below")
                .font(.system(size: 56, weight: .light))
                .foregroundStyle(.tint)

            VStack(spacing: 6) {
                LText("Welcome to FileFluss")
                    .font(.largeTitle.weight(.semibold))
                LText("A few seconds of setup will make FileFluss feel instant.")
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }

            VStack(alignment: .leading, spacing: 14) {
                bullet(
                    icon: "lock.open.fill",
                    title: L10n.text("Grant Full Disk Access (recommended)"),
                    text: L10n.text("FileFluss can browse every folder instantly, with no per-folder permission prompts.")
                )
                bullet(
                    icon: "hand.raised.fill",
                    title: L10n.text("Or continue without it"),
                    text: L10n.text("macOS will ask once per protected folder (Desktop, Documents, Downloads, …). The first open of each may take a few seconds — but only the first time.")
                )
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // MARK: - Drag instructions (after Settings is opened)

    private var dragInstructionsContent: some View {
        VStack(spacing: 18) {
            LText("Add FileFluss to Full Disk Access")
                .font(.title.weight(.semibold))

            // Text(.init(...)) renders the markdown bold in the *translated*
            // string (LText/Text(String) would not parse markdown).
            Text(.init(L10n.text("System Settings is now open. Drag the FileFluss icon below into the **Full Disk Access** list — then click **Quit & Relaunch** so the new permission takes effect.")))
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            DraggableAppIcon()
                .padding(.vertical, 8)

            Text(.init(L10n.text("Or click the **+** button in System Settings and pick FileFluss from your Applications folder.")))
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func bullet(icon: String, title: String, text: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(.tint)
                .frame(width: 24)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(text)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func finish() {
        hasCompletedWelcome = true
        dismiss()
    }

    private func openFullDiskAccessSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Asks LaunchServices to spawn a new instance of FileFluss, then quits
    /// this one. `createsNewApplicationInstance = true` makes LS launch the
    /// second copy as a fully independent process — so our termination
    /// doesn't kill it. A brief delay before `terminate` gives LS time to
    /// dispatch the launch request.
    private func quitAndRelaunch() {
        hasCompletedWelcome = true
        let bundleURL = Bundle.main.bundleURL
        NSLog("[FileFluss] quitAndRelaunch → relaunching from \(bundleURL.path)")

        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        config.activates = true

        NSWorkspace.shared.openApplication(at: bundleURL, configuration: config) { app, error in
            if let error {
                NSLog("[FileFluss] openApplication failed: \(error.localizedDescription)")
            } else if let app {
                NSLog("[FileFluss] new instance launched pid=\(app.processIdentifier)")
            }
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            NSApp.terminate(nil)
        }
    }
}

/// Shows the FileFluss app icon and lets the user drag the running .app bundle
/// into the Full Disk Access list in System Settings. Drag payload is a file
/// URL pointing at `Bundle.main.bundleURL` so System Settings adds the correct
/// app even when multiple copies exist.
private struct DraggableAppIcon: View {
    @State private var isHovering = false

    var body: some View {
        VStack(spacing: 8) {
            Image(nsImage: appIcon)
                .resizable()
                .interpolation(.high)
                .frame(width: 96, height: 96)
                .shadow(color: .black.opacity(0.18), radius: 6, y: 3)
                .scaleEffect(isHovering ? 1.04 : 1.0)
                .animation(.easeOut(duration: 0.15), value: isHovering)
                .onHover { isHovering = $0 }
                .onDrag {
                    NSItemProvider(object: Bundle.main.bundleURL as NSURL)
                }
                .help(L10n.text("Drag into the Full Disk Access list"))

            Label(L10n.text("Drag me into the list"), systemImage: "hand.draw.fill")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 14)
        .padding(.horizontal, 28)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(Color.secondary.opacity(0.08))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
                .foregroundStyle(.secondary.opacity(0.35))
        )
    }

    private var appIcon: NSImage {
        NSWorkspace.shared.icon(forFile: Bundle.main.bundlePath)
    }
}
