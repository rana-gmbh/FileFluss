import Foundation

/// Marks files as having come from the internet, which is what makes macOS
/// run its Gatekeeper check when one is opened.
///
/// A browser does this for everything it downloads. An app has to do it
/// itself, and without it a `.dmg` or `.pkg` from a shared folder would open
/// with no check at all.
enum Quarantine {

    static func mark(_ url: URL) {
        var values = URLResourceValues()
        values.quarantineProperties = [
            "LSQuarantineAgentName": "FileFluss",
            "LSQuarantineType": "LSQuarantineTypeOtherDownload",
        ]
        var target = url
        try? target.setResourceValues(values)
    }

    static func isMarked(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.quarantinePropertiesKey]))?.quarantineProperties != nil
    }
}
