import Testing
import Foundation
@testable import FileFluss

/// Keeps the four translations in step with the strings the app actually
/// asks for.
///
/// A sweep found 112 strings that went through `L10n` or `LText` but existed
/// in no `.strings` file at all — Settings labels, the Storage Analysis
/// window, most of the share-link sheet, and the messages shown when an
/// account needs re-authenticating. They reached German and Chinese users in
/// English. A one-off fix would have lasted until the next feature, so the
/// rule is checked here instead.
@Suite("Localization coverage")
struct LocalizationCoverageTests {

    private static let languages = ["en", "de", "zh-Hans", "zh-Hant"]

    /// The repository, found from this file rather than from a bundle: the
    /// point is to read the sources, not what was compiled.
    private static let repositoryRoot: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // FileFlussTests
        .deletingLastPathComponent()   // repository root

    private static func stringsTable(_ language: String) throws -> [String: String] {
        let url = repositoryRoot
            .appendingPathComponent("FileFluss/Resources/\(language).lproj/Localizable.strings")
        let data = try Data(contentsOf: url)
        let parsed = try PropertyListSerialization.propertyList(from: data, format: nil)
        return parsed as? [String: String] ?? [:]
    }

    /// Every `L10n.text("…")`, `L10n.format("…")` and `LText("…")` in the
    /// app and the package.
    private static func keysUsedInSource() throws -> [String: Set<String>] {
        let pattern = try NSRegularExpression(
            pattern: #"(?:L10n\.(?:text|format)|LText)\(\s*"((?:[^"\\]|\\.)*)""#
        )
        var keys: [String: Set<String>] = [:]
        for directory in ["FileFluss", "Packages"] {
            let root = repositoryRoot.appendingPathComponent(directory)
            guard let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil) else { continue }
            while let url = walker.nextObject() as? URL {
                guard url.pathExtension == "swift" else { continue }
                let path = url.path
                guard !path.contains("/Tests/"), !path.contains("DerivedData") else { continue }
                guard let source = try? String(contentsOf: url, encoding: .utf8) else { continue }
                let range = NSRange(source.startIndex..<source.endIndex, in: source)
                for match in pattern.matches(in: source, range: range) {
                    guard let r = Range(match.range(at: 1), in: source) else { continue }
                    let key = String(source[r])
                        .replacingOccurrences(of: "\\\"", with: "\"")
                        .replacingOccurrences(of: "\\n", with: "\n")
                    guard !key.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
                    keys[key, default: []].insert(url.lastPathComponent)
                }
            }
        }
        return keys
    }

    /// A key the app asks for but no file defines falls back to the key
    /// itself, so it reads as English in every language — including German,
    /// where nothing looks obviously wrong.
    @Test("Every string the app asks for exists in English")
    func englishCoversTheSource() throws {
        let english = try Self.stringsTable("en")
        let used = try Self.keysUsedInSource()

        let missing = used.keys.filter { english[$0] == nil }.sorted()
        #expect(missing.isEmpty, """
        \(missing.count) string(s) used in code are in no .strings file, so they appear in English everywhere:
        \(missing.prefix(20).map { "  • \($0)  (\(used[$0]!.sorted().joined(separator: ", ")))" }.joined(separator: "\n"))
        """)
    }

    /// The translations are judged against English, so a string added to one
    /// language and forgotten in another shows up here rather than in
    /// someone's window.
    @Test("Every language covers English")
    func translationsCoverEnglish() throws {
        let english = Set(try Self.stringsTable("en").keys)

        for language in Self.languages where language != "en" {
            let translated = Set(try Self.stringsTable(language).keys)
            let missing = english.subtracting(translated).sorted()
            #expect(missing.isEmpty, """
            \(language) is missing \(missing.count) string(s):
            \(missing.prefix(20).map { "  • \($0)" }.joined(separator: "\n"))
            """)
        }
    }

    /// A format string whose placeholders differ between languages crashes
    /// or prints nonsense at the moment it is shown, which is the worst
    /// time to find out.
    ///
    /// Compared by *argument position*, not by the literal text: a
    /// translation is free — and often obliged, when word order changes —
    /// to write `%2$@ %1$@` where English writes `%@ %@`. What must not
    /// change is which argument each position consumes and how it is
    /// formatted.
    @Test("Placeholders match across languages")
    func placeholdersMatch() throws {
        let english = try Self.stringsTable("en")
        let specifier = try NSRegularExpression(
            pattern: #"%(?:(\d+)\$)?([-+ #0]*[\d.*]*(?:hh|h|ll|l|q|L|z|t|j)?)([@dDuUxXoOfeEgGcCsSpaAF%])"#
        )

        /// Maps each argument position to the conversion it expects, e.g.
        /// `"%@ is %.1f%% full"` → `[1: "@", 2: ".1f"]`. A literal `%%`
        /// consumes no argument and is skipped.
        func specifiers(_ value: String) -> [Int: String] {
            let range = NSRange(value.startIndex..<value.endIndex, in: value)
            var result: [Int: String] = [:]
            var implicitPosition = 0
            for match in specifier.matches(in: value, range: range) {
                func group(_ i: Int) -> String? {
                    Range(match.range(at: i), in: value).map { String(value[$0]) }
                }
                guard let conversion = group(3), conversion != "%" else { continue }
                let position: Int
                if let explicit = group(1).flatMap(Int.init) {
                    position = explicit
                } else {
                    implicitPosition += 1
                    position = implicitPosition
                }
                result[position] = (group(2) ?? "") + conversion
            }
            return result
        }

        for language in Self.languages where language != "en" {
            let table = try Self.stringsTable(language)
            for (key, englishValue) in english {
                guard let translated = table[key] else { continue }
                let expected = specifiers(englishValue)
                let actual = specifiers(translated)
                #expect(expected == actual, """
                \(language): placeholders differ for "\(key)"
                  english:    \(expected.sorted { $0.key < $1.key })
                  translated: \(actual.sorted { $0.key < $1.key })
                """)
            }
        }
    }

    /// The checks above only see text that goes through `L10n`. A second
    /// sweep found about 115 texts written straight into the code — dialog
    /// titles, "Failed to delete …" messages, provider errors — which no
    /// table could translate. These are the places they turned up.
    @Test("Dialogs and error messages don't hard-code English")
    func noHardcodedEnglish() throws {
        let english = try Self.stringsTable("en")
        let literal = #""((?=[^"\\]*[A-Za-z]{2})(?:[^"\\]|\\.)*)""#
        let rules: [(name: String, pattern: NSRegularExpression)] = try [
            ("dialog title", NSRegularExpression(pattern: #"\.(?:confirmationDialog|alert)\(\s*"# + literal)),
            ("error message", NSRegularExpression(pattern: #"\b(?:error|actionError|errorMessage|mountErrorMessage|authError|statusMessage)\s*=\s*"# + literal)),
            ("Text or Button", NSRegularExpression(pattern: #"(?<![\w.])(?:Text|Button)\(\s*"# + literal)),
            ("Text or Button choice", NSRegularExpression(pattern: #"(?<![\w.])(?:Text|Button)\([^()]*\?\s*"# + literal)),
            ("error description", NSRegularExpression(pattern: #"\breturn\s+"# + literal)),
        ]
        // Credits, links and brand names in the About window stay as they are.
        let exempt: Set<String> = ["AboutView.swift", "VersionTestRunner.swift", "TestAccountSnapshot.swift", "Localization.swift"]

        var found: [String] = []
        for directory in ["FileFluss", "Packages/FileFlussCore/Sources"] {
            let root = Self.repositoryRoot.appendingPathComponent(directory)
            guard let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil) else { continue }
            while let url = walker.nextObject() as? URL {
                guard url.pathExtension == "swift", !exempt.contains(url.lastPathComponent),
                      let source = try? String(contentsOf: url, encoding: .utf8) else { continue }
                var debugDepth = 0
                var errorDescriptionDepth: Int?
                var braceDepth = 0
                for (index, line) in source.components(separatedBy: "\n").enumerated() {
                    let trimmed = line.trimmingCharacters(in: .whitespaces)
                    if trimmed.hasPrefix("#if DEBUG") { debugDepth += 1 }
                    if trimmed.hasPrefix("#endif"), debugDepth > 0 { debugDepth -= 1 }
                    if trimmed.contains("var errorDescription") { errorDescriptionDepth = braceDepth }
                    braceDepth += line.filter { $0 == "{" }.count - line.filter { $0 == "}" }.count
                    if let depth = errorDescriptionDepth, braceDepth <= depth, !trimmed.contains("var errorDescription") {
                        errorDescriptionDepth = nil
                    }
                    guard debugDepth == 0, !trimmed.hasPrefix("//"), !line.contains("L10n."), !line.contains("LText(") else { continue }
                    let range = NSRange(line.startIndex..<line.endIndex, in: line)
                    for rule in rules {
                        if rule.name == "error description", errorDescriptionDepth == nil { continue }
                        // SwiftUI looks a plain literal up in the table itself.
                        if let match = rule.pattern.firstMatch(in: line, range: range) {
                            if let r = Range(match.range(at: 1), in: line),
                               english[String(line[r]).replacingOccurrences(of: "\\\"", with: "\"")] != nil { continue }
                            found.append("\(url.lastPathComponent):\(index + 1) (\(rule.name)): \(trimmed.prefix(100))")
                            break
                        }
                    }
                }
            }
        }
        #expect(found.isEmpty, """
        \(found.count) English text(s) written straight into the code, which no translation can reach:
        \(found.prefix(30).map { "  • \($0)" }.joined(separator: "\n"))
        """)
    }
}
