// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Sync;

/// <summary>Which way a saved sync rule copies. JSON names are the macOS raw values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncDirection>))]
public enum SyncDirection
{
    [JsonStringEnumMemberName("upload")] Upload,
    [JsonStringEnumMemberName("download")] Download,
    [JsonStringEnumMemberName("bidirectional")] Bidirectional,
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncStatus>))]
public enum SyncStatus
{
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("syncing")] Syncing,
    [JsonStringEnumMemberName("paused")] Paused,
    [JsonStringEnumMemberName("error")] Error,
}

public static class SyncRuleExtensions
{
    public static string DisplayName(this SyncDirection direction) => direction switch
    {
        SyncDirection.Upload => L("Upload Only"),
        SyncDirection.Download => L("Download Only"),
        _ => L("Bidirectional"),
    };

    /// <summary>Glyph name, in the same SF-Symbol vocabulary the app's icon map resolves.</summary>
    public static string Icon(this SyncDirection direction) => direction switch
    {
        SyncDirection.Upload => "arrow.up.circle",
        SyncDirection.Download => "arrow.down.circle",
        _ => "arrow.up.arrow.down.circle",
    };

    public static string DisplayName(this SyncStatus status) => status switch
    {
        SyncStatus.Idle => L("Up to date"),
        SyncStatus.Syncing => L("Syncing..."),
        SyncStatus.Paused => L("Paused"),
        _ => L("Error"),
    };
}

/// <summary>
/// A saved pairing of a local folder with a cloud folder (macOS <c>SyncRule</c>, FileFlussCore).
/// The macOS app models it but runs no scheduler for it yet; it is ported so a rules list
/// written by either platform reads the same. <see cref="LocalPath"/> is a native path here
/// (macOS stores a file URL).
/// </summary>
public sealed record SyncRule
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("localPath")]
    public required string LocalPath { get; init; }

    [JsonPropertyName("remotePath")]
    public required string RemotePath { get; init; }

    [JsonPropertyName("accountId")]
    public required Guid AccountId { get; init; }

    [JsonPropertyName("direction")]
    public SyncDirection Direction { get; init; } = SyncDirection.Bidirectional;

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;

    [JsonPropertyName("status")]
    public SyncStatus Status { get; init; } = SyncStatus.Idle;

    [JsonPropertyName("excludePatterns")]
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    [JsonPropertyName("lastSyncDate")]
    public DateTimeOffset? LastSyncDate { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}
