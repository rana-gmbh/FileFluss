// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// Common AWS regions for the Add Account picker. The field also takes any typed region
/// code, so this is a convenience list, not an allowlist.
/// </summary>
internal static class S3RegionList
{
    public static readonly IReadOnlyList<(string Code, string DisplayName)> AllRegions =
    [
        ("us-east-1", "US East (N. Virginia)"),
        ("us-east-2", "US East (Ohio)"),
        ("us-west-1", "US West (N. California)"),
        ("us-west-2", "US West (Oregon)"),
        ("eu-central-1", "Europe (Frankfurt)"),
        ("eu-central-2", "Europe (Zurich)"),
        ("eu-west-1", "Europe (Ireland)"),
        ("eu-west-2", "Europe (London)"),
        ("eu-west-3", "Europe (Paris)"),
        ("eu-north-1", "Europe (Stockholm)"),
        ("eu-south-1", "Europe (Milan)"),
        ("ap-northeast-1", "Asia Pacific (Tokyo)"),
        ("ap-northeast-2", "Asia Pacific (Seoul)"),
        ("ap-northeast-3", "Asia Pacific (Osaka)"),
        ("ap-southeast-1", "Asia Pacific (Singapore)"),
        ("ap-southeast-2", "Asia Pacific (Sydney)"),
        ("ap-south-1", "Asia Pacific (Mumbai)"),
        ("sa-east-1", "South America (São Paulo)"),
        ("ca-central-1", "Canada (Central)"),
        ("af-south-1", "Africa (Cape Town)"),
        ("me-south-1", "Middle East (Bahrain)"),
        ("me-central-1", "Middle East (UAE)"),
    ];

    /// <summary>The picker entries: "Europe (Frankfurt) (eu-central-1)" → "eu-central-1", as on macOS.</summary>
    public static IReadOnlyList<ConnectChoice> Choices { get; } =
        AllRegions.Select(r => new ConnectChoice(r.Code, $"{r.DisplayName} ({r.Code})")).ToList();
}
