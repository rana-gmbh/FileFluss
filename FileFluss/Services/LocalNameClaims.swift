import Foundation
import FileFlussCore

/// Names for files coming down from a cloud into one local folder.
///
/// A cloud folder can hold names this Mac stores as one: `README.md` and
/// `Readme.md` on a case-sensitive server (S3, Nextcloud, SFTP) when the
/// local disk ignores case, two files of exactly one name in Google Drive,
/// or the NFC and NFD spellings of `Café`, which every Mac file system
/// treats as the same name. Written under their own names, the second
/// download silently replaced the first, and a cut then deleted the
/// original from the cloud because the folder counted as downloaded.
///
/// The rule is the sync planner's: the first name in ordinal order keeps
/// its name, each later one becomes `name (2)`, `name (3)` and so on,
/// skipping any name a sibling already has. It is decided from the names
/// alone, so the same folder gets the same names every time.
enum LocalNameClaims {

    /// Whether names on the volume holding `directory` differ by case.
    /// When it can't be told, the answer is no: treating two names as one
    /// costs a "(2)", treating one name as two costs a file.
    static func isCaseSensitive(_ directory: URL) -> Bool {
        (try? directory.resourceValues(forKeys: [.volumeSupportsCaseSensitiveNamesKey]))?
            .volumeSupportsCaseSensitiveNames ?? false
    }

    /// What two names have in common when the disk stores them as one.
    /// Unicode form is always folded — APFS and HFS+ both ignore it — and
    /// case only where the volume ignores case.
    static func key(for name: String, caseSensitive: Bool) -> String {
        let form = name.precomposedStringWithCanonicalMapping
        return caseSensitive ? form : form.folding(options: [.caseInsensitive], locale: nil)
    }

    /// The local name for each of `names`, in the order given. Names are
    /// sanitised the way `SafeLocalPath` sanitises them, since that is the
    /// name that reaches the disk and two remote names can become one there.
    static func localNames(for names: [String], caseSensitive: Bool) -> [String] {
        let safe = names.map(SafeLocalPath.fileName(from:))
        func key(_ name: String) -> String { Self.key(for: name, caseSensitive: caseSensitive) }

        // Every sibling's own name is reserved up front, so a "(2)" never
        // lands on a name that a real file of the folder carries.
        var taken = Set(safe.map(key))
        var claimed: Set<String> = []
        var result = safe
        // Ordinal order: by the bytes of the original names, which keeps
        // NFC and NFD spellings apart (Swift's `<` treats them as equal).
        // Equal names keep the order the listing gave them.
        let order = names.indices.sorted { a, b in
            if names[a].utf8.elementsEqual(names[b].utf8) { return a < b }
            return names[a].utf8.lexicographicallyPrecedes(names[b].utf8)
        }
        for index in order {
            let name = safe[index]
            if claimed.insert(key(name)).inserted { continue }
            let unique = uniqueName(for: name) { taken.contains(key($0)) }
            taken.insert(key(unique))
            claimed.insert(key(unique))
            result[index] = unique
        }
        return result
    }

    /// `name (2).ext`, `name (3).ext`, … — the first that `isTaken` allows.
    static func uniqueName(for name: String, isTaken: (String) -> Bool) -> String {
        let dot = name.lastIndex(of: ".")
        let (stem, ext): (Substring, Substring)
        if let dot, dot > name.startIndex {
            (stem, ext) = (name[..<dot], name[dot...])
        } else {
            (stem, ext) = (name[...], "")
        }
        var counter = 2
        while true {
            let candidate = "\(stem) (\(counter))\(ext)"
            if !isTaken(candidate) { return candidate }
            counter += 1
        }
    }
}
