// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Models;

/// <summary>
/// A connected account as the sidebar lists it. Credentials never live here — they sit in
/// the credential store under a key derived from <see cref="Id"/>, exactly as the macOS app
/// keeps them in the keychain.
/// </summary>
public sealed record CloudAccount
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("providerType")]
    public required CloudProviderType ProviderType { get; init; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether a live provider is registered for the account right now.</summary>
    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; init; }

    /// <summary>The folder a panel opens at — "/" for most, a remote home for SFTP, a bucket prefix for S3.</summary>
    [JsonPropertyName("rootPath")]
    public string RootPath { get; init; } = "/";

    [JsonPropertyName("lastSyncDate")]
    public DateTimeOffset? LastSyncDate { get; init; }

    /// <summary>
    /// User-forced offline mode: the panel reads the cached file tree from the search index
    /// instead of talking to the provider. Distinct from <see cref="IsConnected"/>.
    /// </summary>
    [JsonPropertyName("isOfflineMode")]
    public bool IsOfflineMode { get; init; }

    /// <summary>Offline in the sense the UI cares about: forced offline, or not connected.</summary>
    [JsonIgnore]
    public bool IsEffectivelyOffline => IsOfflineMode || !IsConnected;

    /// <summary>"/" when the stored root is empty, which is how a panel treats it.</summary>
    [JsonIgnore]
    public string EffectiveRootPath => string.IsNullOrEmpty(RootPath) ? "/" : RootPath;

    /// <summary>"Dropbox (Robert Rudolph)" — how macOS names a freshly connected account.</summary>
    public static string ComposeDisplayName(CloudProviderType type, string? accountLabel)
        => string.IsNullOrWhiteSpace(accountLabel) || accountLabel == "Unknown"
            ? type.DisplayName()
            : $"{type.DisplayName()} ({accountLabel.Trim()})";
}
