import Foundation

/// Static catalogue of common AWS S3 regions used by the Add-Account
/// picker. The user can also paste an arbitrary region code into the
/// fallback text field, so this is just a convenience list — not a
/// gating allowlist.
enum S3RegionList {
    struct Region: Sendable {
        let code: String
        let displayName: String
    }

    static let allRegions: [Region] = [
        Region(code: "us-east-1", displayName: "US East (N. Virginia)"),
        Region(code: "us-east-2", displayName: "US East (Ohio)"),
        Region(code: "us-west-1", displayName: "US West (N. California)"),
        Region(code: "us-west-2", displayName: "US West (Oregon)"),
        Region(code: "eu-central-1", displayName: "Europe (Frankfurt)"),
        Region(code: "eu-central-2", displayName: "Europe (Zurich)"),
        Region(code: "eu-west-1", displayName: "Europe (Ireland)"),
        Region(code: "eu-west-2", displayName: "Europe (London)"),
        Region(code: "eu-west-3", displayName: "Europe (Paris)"),
        Region(code: "eu-north-1", displayName: "Europe (Stockholm)"),
        Region(code: "eu-south-1", displayName: "Europe (Milan)"),
        Region(code: "ap-northeast-1", displayName: "Asia Pacific (Tokyo)"),
        Region(code: "ap-northeast-2", displayName: "Asia Pacific (Seoul)"),
        Region(code: "ap-northeast-3", displayName: "Asia Pacific (Osaka)"),
        Region(code: "ap-southeast-1", displayName: "Asia Pacific (Singapore)"),
        Region(code: "ap-southeast-2", displayName: "Asia Pacific (Sydney)"),
        Region(code: "ap-south-1", displayName: "Asia Pacific (Mumbai)"),
        Region(code: "sa-east-1", displayName: "South America (São Paulo)"),
        Region(code: "ca-central-1", displayName: "Canada (Central)"),
        Region(code: "af-south-1", displayName: "Africa (Cape Town)"),
        Region(code: "me-south-1", displayName: "Middle East (Bahrain)"),
        Region(code: "me-central-1", displayName: "Middle East (UAE)"),
    ]
}
