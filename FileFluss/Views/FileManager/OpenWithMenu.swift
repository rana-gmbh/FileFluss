import AppKit
import UniformTypeIdentifiers
import FileFlussCore

/// The applications that can open a selection, the way Finder's
/// "Open With" lists them: the default first, the rest by name.
///
/// Asked about local files by URL where there is one — that is what lets a
/// per-file "always open with" choice made in Finder show up as the
/// default — and by content type otherwise: a cloud file or an archive
/// entry has no file on disk yet, only a name.
enum OpenWithCandidates {
    struct App: Equatable {
        let url: URL
        let name: String
        let isDefault: Bool
    }

    enum Source {
        case files([URL])
        case types([UTType])
    }

    /// Apps that can open *every* item in `source`, as Finder offers for a
    /// multi-selection. Empty when nothing on this Mac claims the type.
    static func apps(for source: Source) -> [App] {
        let workspace = NSWorkspace.shared
        let perItem: [[URL]]
        let defaultApp: URL?
        switch source {
        case .files(let urls):
            guard !urls.isEmpty else { return [] }
            perItem = urls.map { workspace.urlsForApplications(toOpen: $0) }
            defaultApp = workspace.urlForApplication(toOpen: urls[0])
        case .types(let types):
            guard !types.isEmpty else { return [] }
            perItem = types.map { workspace.urlsForApplications(toOpen: $0) }
            defaultApp = workspace.urlForApplication(toOpen: types[0])
        }
        return rank(perItem: perItem, defaultApp: defaultApp, name: displayName(of:))
    }

    /// Intersects, de-duplicates and orders the candidates. Split out from
    /// the LaunchServices calls so the rules can be tested on their own.
    static func rank(
        perItem: [[URL]], defaultApp: URL?, name: (URL) -> String
    ) -> [App] {
        guard var common = perItem.first.map({ Set($0.map(\.standardizedFileURL)) }) else { return [] }
        for urls in perItem.dropFirst() {
            common.formIntersection(urls.map(\.standardizedFileURL))
        }
        let standardDefault = defaultApp?.standardizedFileURL
        // The default only leads when every item agrees it can open them.
        let leader = standardDefault.flatMap { common.contains($0) ? $0 : nil }

        // Two copies of the same app (an old version beside the new one, or
        // the same app on two volumes) would otherwise read as duplicates;
        // Finder shows them with their versions, which is more than a
        // context menu needs. Keep the default, or else the first seen.
        var seenNames: Set<String> = []
        var rest: [App] = []
        if let leader { seenNames.insert(name(leader)) }
        for url in common.sorted(by: { name($0).localizedStandardCompare(name($1)) == .orderedAscending })
        where url != leader {
            let appName = name(url)
            guard seenNames.insert(appName).inserted else { continue }
            rest.append(App(url: url, name: appName, isDefault: false))
        }
        let first = leader.map { [App(url: $0, name: name($0), isDefault: true)] } ?? []
        return first + rest
    }

    static func displayName(of app: URL) -> String {
        let name = FileManager.default.displayName(atPath: app.path)
        return name.hasSuffix(".app") ? String(name.dropLast(4)) : name
    }
}

/// The "Open With" submenu, shared by the local and cloud panels.
///
/// Filled in when it is opened rather than when the context menu is built:
/// asking LaunchServices for every app that handles a type costs a few
/// milliseconds per item, and most right-clicks never open this submenu.
@MainActor
final class OpenWithMenu: NSObject, NSMenuDelegate {
    private let source: OpenWithCandidates.Source
    private let onChoose: (URL) -> Void

    private init(source: OpenWithCandidates.Source, onChoose: @escaping (URL) -> Void) {
        self.source = source
        self.onChoose = onChoose
    }

    /// `onChoose` is called with the application the user picked.
    static func item(
        for source: OpenWithCandidates.Source, onChoose: @escaping (URL) -> Void
    ) -> NSMenuItem {
        let controller = OpenWithMenu(source: source, onChoose: onChoose)
        let parent = NSMenuItem(title: L10n.text("Open With"), action: nil, keyEquivalent: "")
        let submenu = NSMenu(title: L10n.text("Open With"))
        // A menu holds its delegate weakly; the item holds the controller.
        submenu.delegate = controller
        parent.representedObject = controller
        parent.submenu = submenu
        return parent
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let apps = OpenWithCandidates.apps(for: source)

        if apps.isEmpty {
            let none = NSMenuItem(title: L10n.text("No Suitable Applications"), action: nil, keyEquivalent: "")
            none.isEnabled = false
            menu.addItem(none)
        }
        for app in apps {
            let title = app.isDefault ? L10n.format("%@ (default)", app.name) : app.name
            let item = NSMenuItem(title: title, action: #selector(choose(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = app.url
            let icon = NSWorkspace.shared.icon(forFile: app.url.path)
            icon.size = NSSize(width: 16, height: 16)
            item.image = icon
            menu.addItem(item)
            // Finder sets the default apart from the alternatives.
            if app.isDefault, apps.count > 1 { menu.addItem(.separator()) }
        }

        menu.addItem(.separator())
        let other = NSMenuItem(title: L10n.text("Other…"), action: #selector(chooseOther(_:)), keyEquivalent: "")
        other.target = self
        menu.addItem(other)
    }

    @objc private func choose(_ sender: NSMenuItem) {
        guard let app = sender.representedObject as? URL else { return }
        onChoose(app)
    }

    /// Any application at all, picked from /Applications — for the file
    /// whose type no installed app has claimed.
    @objc private func chooseOther(_ sender: NSMenuItem) {
        let panel = NSOpenPanel()
        panel.title = L10n.text("Choose Application")
        panel.prompt = L10n.text("Open")
        panel.allowedContentTypes = [.application]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.directoryURL = URL(fileURLWithPath: "/Applications", isDirectory: true)
        guard panel.runModal() == .OK, let app = panel.url else { return }
        onChoose(app)
    }
}

extension NSWorkspace {
    /// Opens `files` in `app`, reporting a failure the way Finder does.
    @MainActor
    func open(_ files: [URL], withApplication app: URL) {
        guard !files.isEmpty else { return }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        open(files, withApplicationAt: app, configuration: configuration) { _, error in
            guard let error else { return }
            SupportLogger.shared.log(
                "open with \(app.lastPathComponent) failed: \(error.localizedDescription)",
                category: "fs", level: .error
            )
            DispatchQueue.main.async { NSAlert(error: error).runModal() }
        }
    }
}
