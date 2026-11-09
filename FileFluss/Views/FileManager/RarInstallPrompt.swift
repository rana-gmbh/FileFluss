import SwiftUI
import FileFlussCore

/// The offer to install RARLAB's rar tool, and what came of it.
///
/// Attached once to the main window: the request can come from either
/// panel's Compress menu, and one prompt is enough.
struct RarInstallPrompt: ViewModifier {
    private enum Stage: Equatable {
        case offer
        case installed
        case failed(String)
    }

    @State private var stage: Stage?
    @State private var isInstalling = false

    func body(content: Content) -> some View {
        content
            .onReceive(NotificationCenter.default.publisher(for: .requestRarInstall)) { _ in
                guard !isInstalling else { return }
                stage = .offer
            }
            .alert(
                title,
                isPresented: Binding(get: { stage != nil }, set: { if !$0 { stage = nil } }),
                presenting: stage
            ) { stage in
                switch stage {
                case .offer:
                    Button(L10n.text("Download and Install")) { install() }
                    Button(L10n.text("Open rarlab.com")) { NSWorkspace.shared.open(RarInstaller.downloadPage) }
                    Button(L10n.text("Cancel"), role: .cancel) {}
                case .installed:
                    Button(L10n.text("OK"), role: .cancel) {}
                case .failed:
                    Button(L10n.text("Open rarlab.com")) { NSWorkspace.shared.open(RarInstaller.downloadPage) }
                    Button(L10n.text("OK"), role: .cancel) {}
                }
            } message: { stage in
                switch stage {
                case .offer:
                    Text(L10n.format(
                        "FileFluss can download RAR %@ for macOS from rarlab.com (under 1 MB), check it against a known fingerprint, and keep it in its own folder. No administrator password is needed.\n\nRAR is trial software from RARLAB: it may be used for up to 40 days, after which a licence has to be bought from rarlab.com.",
                        RarInstaller.version
                    ))
                case .installed:
                    Text(L10n.text("Choose Compress › RAR to make RAR archives."))
                case .failed(let message):
                    Text(message)
                }
            }
    }

    private var title: String {
        switch stage {
        case .offer, nil: return L10n.text("Install the rar tool?")
        case .installed: return L10n.text("The rar tool is installed")
        case .failed: return L10n.text("The rar tool could not be installed")
        }
    }

    private func install() {
        isInstalling = true
        Task {
            do {
                try await RarInstaller.install()
                stage = .installed
            } catch {
                SupportLogger.shared.log(
                    "rar install failed: \(error.localizedDescription)", category: "archive", level: .error
                )
                stage = .failed(error.localizedDescription)
            }
            isInstalling = false
        }
    }
}

extension View {
    func rarInstallPrompt() -> some View { modifier(RarInstallPrompt()) }
}
