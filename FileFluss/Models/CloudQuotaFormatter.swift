import Foundation
import FileFlussCore

/// Renders a CloudStorageQuota into the short string we show in the
/// cloud panel's status bar and as the sidebar-row tooltip.
///
/// Examples:
///   "Dropbox: 4.2 GB of 15 GB used (28%)"
///   "Google Drive: 412 KB used"                  (unlimited / no total)
///   "OneDrive: 1 TB of 1 TB used (100%)"         (capped at 100%)
enum CloudQuotaFormatter {
    static func summary(_ quota: CloudStorageQuota, accountDisplayName: String) -> String {
        let used = ByteCountFormatter.string(fromByteCount: quota.usedBytes, countStyle: .file)
        guard let total = quota.totalBytes, total > 0 else {
            return "\(accountDisplayName): \(used) used"
        }
        let totalStr = ByteCountFormatter.string(fromByteCount: total, countStyle: .file)
        let percent = percentString(used: quota.usedBytes, total: total)
        return "\(accountDisplayName): \(used) of \(totalStr) used (\(percent))"
    }

    /// "28%" with 0–100 clamping. Quotas can briefly report > 100% when
    /// a soft limit is exceeded; clamping prevents the visual jolt.
    static func percentString(used: Int64, total: Int64) -> String {
        guard total > 0 else { return "0%" }
        if used <= 0 { return "0%" }
        let raw = Double(used) / Double(total) * 100
        let clamped = max(0, min(100, raw))
        // Round to whole percent for >= 1%, one decimal under 1% so a
        // ~10 MB file on a 1 TB drive doesn't render as a flat 0%.
        if clamped >= 1 {
            return "\(Int(clamped.rounded()))%"
        }
        return String(format: "%.1f%%", clamped)
    }
}
