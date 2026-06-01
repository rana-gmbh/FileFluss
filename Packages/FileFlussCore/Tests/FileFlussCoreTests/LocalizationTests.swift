import Testing
import Foundation
@testable import FileFlussCore

/// Verifies the NetFluss-style localization resolver: a known key returns the
/// translation from the package's `.lproj`, an unknown key falls back to the
/// English key, and `format` substitutes arguments.
/// Serialized: every test mutates the shared `appLanguage` default, so they
/// must not run concurrently.
@Suite("Localization", .serialized)
struct LocalizationTests {

    private func withLanguage(_ code: String, _ body: () -> Void) {
        let key = AppLanguage.storageKey
        let previous = UserDefaults.standard.string(forKey: key)
        UserDefaults.standard.set(code, forKey: key)
        defer {
            if let previous { UserDefaults.standard.set(previous, forKey: key) }
            else { UserDefaults.standard.removeObject(forKey: key) }
        }
        body()
    }

    @Test("Resolves a German package string")
    func germanPackageString() {
        withLanguage("de") {
            #expect(L10n.text("Storage quota exceeded.") == "Speicherkontingent überschritten.")
        }
    }

    @Test("Resolves a Simplified Chinese package string")
    func simplifiedChinesePackageString() {
        withLanguage("zh-Hans") {
            #expect(L10n.text("Invalid response from server.") == "服务器返回了无效响应。")
        }
    }

    @Test("Unknown key falls back to the English key")
    func fallbackToKey() {
        withLanguage("de") {
            #expect(L10n.text("__no_such_key__") == "__no_such_key__")
        }
    }

    @Test("format substitutes arguments")
    func formatSubstitution() {
        withLanguage("en") {
            #expect(L10n.format("Item not found: %@", "/tmp/x") == "Item not found: /tmp/x")
        }
        withLanguage("de") {
            #expect(L10n.format("Item not found: %@", "/tmp/x") == "Objekt nicht gefunden: /tmp/x")
        }
    }

    @Test("AppLanguage.selected reflects the stored value")
    func selectedReflectsStorage() {
        withLanguage("zh-Hant") {
            #expect(AppLanguage.selected == .traditionalChinese)
        }
    }
}
