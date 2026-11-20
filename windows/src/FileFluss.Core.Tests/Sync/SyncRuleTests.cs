// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using FileFluss.Core.Sync;

namespace FileFluss.Core.Tests.Sync;

/// <summary>Port of the macOS SyncRuleTests.</summary>
[Collection("Localization")]
public class SyncRuleTests
{
    [Fact]
    public void ARuleStartsWithTheMacDefaults()
    {
        var rule = new SyncRule { LocalPath = @"C:\Users\test\Documents", RemotePath = "/Backup/Documents", AccountId = Guid.NewGuid() };

        Assert.Equal(SyncDirection.Bidirectional, rule.Direction);
        Assert.True(rule.IsEnabled);
        Assert.Equal(SyncStatus.Idle, rule.Status);
        Assert.Empty(rule.ExcludePatterns);
        Assert.Null(rule.LastSyncDate);
        Assert.Null(rule.ErrorMessage);
    }

    [Fact]
    public void DirectionsHaveNamesAndIcons()
    {
        foreach (var direction in Enum.GetValues<SyncDirection>())
        {
            Assert.False(string.IsNullOrEmpty(direction.DisplayName()));
            Assert.False(string.IsNullOrEmpty(direction.Icon()));
        }
    }

    [Fact]
    public void StatusesHaveTheMacNames()
    {
        Localization.Use(AppLanguage.English);
        Assert.Equal("Up to date", SyncStatus.Idle.DisplayName());
        Assert.Equal("Syncing...", SyncStatus.Syncing.DisplayName());
        Assert.Equal("Paused", SyncStatus.Paused.DisplayName());
        Assert.Equal("Error", SyncStatus.Error.DisplayName());
    }

    [Fact]
    public void ARuleRoundTripsThroughJsonWithTheMacKeys()
    {
        var original = new SyncRule
        {
            LocalPath = @"C:\Users\test\Documents",
            RemotePath = "/Backup",
            AccountId = Guid.NewGuid(),
            Direction = SyncDirection.Upload,
            IsEnabled = false,
            ExcludePatterns = ["*.tmp", "Thumbs.db"],
        };

        var json = JsonSerializer.Serialize(original);
        var decoded = JsonSerializer.Deserialize<SyncRule>(json)!;

        Assert.Contains("\"direction\":\"upload\"", json);
        Assert.Contains("\"excludePatterns\"", json);
        Assert.Equal(original.Id, decoded.Id);
        Assert.Equal(original.LocalPath, decoded.LocalPath);
        Assert.Equal(original.RemotePath, decoded.RemotePath);
        Assert.Equal(SyncDirection.Upload, decoded.Direction);
        Assert.False(decoded.IsEnabled);
        Assert.Equal(["*.tmp", "Thumbs.db"], decoded.ExcludePatterns);
    }
}
