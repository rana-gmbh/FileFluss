import Foundation
import SwiftUI

/// In-app language selection, mirroring the approach used in NetFluss: the
/// `.strings` keys are the English text itself, and `L10n` / `LText` look the
/// key up in the selected language's `.lproj` bundle, falling back to the
/// English key. The selection is persisted to `UserDefaults["appLanguage"]`
/// and drives live switching through `LocalizedRoot`.
public enum AppLanguage: String, CaseIterable, Identifiable, Sendable {
    case system
    case english = "en"
    case german = "de"
    case simplifiedChinese = "zh-Hans"
    case traditionalChinese = "zh-Hant"

    public var id: String { rawValue }

    /// The UserDefaults key the picker binds to.
    public static let storageKey = "appLanguage"

    public var displayName: String {
        switch self {
        case .system: return "System Default"
        case .english: return "English"
        case .german: return "Deutsch"
        case .simplifiedChinese: return "简体中文"
        case .traditionalChinese: return "繁體中文"
        }
    }

    public var locale: Locale {
        switch self {
        case .system: return .autoupdatingCurrent
        case .english, .german, .simplifiedChinese, .traditionalChinese:
            return Locale(identifier: rawValue)
        }
    }

    public static func current(from rawValue: String) -> AppLanguage {
        AppLanguage(rawValue: rawValue) ?? .system
    }

    public static var selected: AppLanguage {
        current(from: UserDefaults.standard.string(forKey: storageKey) ?? AppLanguage.system.rawValue)
    }

    /// Bundles to search for a translation, in priority order. App UI strings
    /// live in the app bundle (`.main`); package-owned strings (cloud error
    /// messages, etc.) live in `Bundle.module`. For an explicit language we
    /// return that language's `.lproj` inside each; for `.system` we return
    /// the bundles themselves so `NSLocalizedString` resolves per the OS
    /// language as usual.
    public var localizedBundles: [Bundle] {
        switch self {
        case .system:
            return [.main, .module]
        case .english, .german, .simplifiedChinese, .traditionalChinese:
            // SwiftPM lowercases the region subtag when it bundles resources
            // (`zh-Hans` → `zh-hans.lproj`), while Xcode keeps the original
            // case in the app bundle. Try both spellings in each base. We only
            // append the `.lproj` bundles that actually exist; a missing
            // translation then falls through to the English key in `L10n.text`.
            let codes = [rawValue, rawValue.lowercased()]
            var result: [Bundle] = []
            for base in [Bundle.main, Bundle.module] {
                for code in codes {
                    if let path = base.path(forResource: code, ofType: "lproj"),
                       let bundle = Bundle(path: path) {
                        result.append(bundle)
                        break
                    }
                }
            }
            return result
        }
    }

    /// Persist a new selection and mirror it into `AppleLanguages` so the next
    /// launch also localises the menu bar, window titles, and system dialogs
    /// (which SwiftUI/AppKit resolve against the process language, not our
    /// in-app override). In-window content switches live via `LocalizedRoot`.
    public static func apply(_ language: AppLanguage) {
        let defaults = UserDefaults.standard
        defaults.set(language.rawValue, forKey: storageKey)
        switch language {
        case .system:
            defaults.removeObject(forKey: "AppleLanguages")
        case .english, .german, .simplifiedChinese, .traditionalChinese:
            defaults.set([language.rawValue], forKey: "AppleLanguages")
        }
        // Force the write to disk *now*. The relaunch that follows reads
        // AppleLanguages from the persistent store as the new process starts;
        // without this flush it can race and read the previous language,
        // leaving the macOS-provided menu bar in the old language while the
        // in-window content (driven live by appLanguage) is already switched.
        defaults.synchronize()
    }
}

/// String-typed lookups for places that need a `String` rather than a `View`
/// (button titles, `.help`, alert messages, `Window` titles, …).
public enum L10n {
    private static let miss = "\u{0}\u{0}MISS\u{0}\u{0}"

    public static func text(_ key: String) -> String {
        for bundle in AppLanguage.selected.localizedBundles {
            let value = NSLocalizedString(key, tableName: nil, bundle: bundle, value: miss, comment: "")
            if value != miss { return value }
        }
        // Keys are the English text, so the key itself is the English fallback.
        return key
    }

    public static func format(_ key: String, _ arguments: CVarArg...) -> String {
        String(format: text(key), locale: AppLanguage.selected.locale, arguments: arguments)
    }
}

/// Drop-in replacement for `Text("English string")` that re-renders when the
/// in-app language changes.
public struct LText: View {
    @AppStorage(AppLanguage.storageKey) private var appLanguage: String = AppLanguage.system.rawValue
    private let key: String

    public init(_ key: String) {
        self.key = key
    }

    public var body: some View {
        // Touch appLanguage so the view re-renders on change; resolution goes
        // through L10n which reads the (same) current selection.
        _ = appLanguage
        return Text(L10n.text(key))
    }
}

/// Wraps a scene's content so the whole subtree rebuilds (`.id`) and adopts the
/// selected locale when the language changes — giving live in-window switching.
public struct LocalizedRoot<Content: View>: View {
    @AppStorage(AppLanguage.storageKey) private var appLanguage: String = AppLanguage.system.rawValue
    private let content: Content

    public init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    public var body: some View {
        let language = AppLanguage.current(from: appLanguage)
        content
            .environment(\.locale, language.locale)
            .id(appLanguage)
    }
}
