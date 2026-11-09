// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Settings;

namespace FileFluss.Core.Tests;

/// <summary>macOS 1.6 "Debug: keep test accounts apart from the release app, and save them".</summary>
public sealed class TestAccountSnapshotTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ff-testaccounts-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CloudAccount Account(string name) => new() { ProviderType = CloudProviderType.WebDav, DisplayName = name, IsConnected = true };

    [Fact]
    public void WhatIsHereWinsAndWhatIsOnlySavedStays()
    {
        var kept = Account("removed by accident");
        var both = Account("still here");
        var existing = TestAccountSnapshot.Merge([kept, both], new Dictionary<string, byte[]> { ["a.bin"] = [1], ["b.bin"] = [2] }, null, DateTimeOffset.UnixEpoch);

        var renamed = both with { DisplayName = "renamed" };
        var added = Account("new");
        var merged = TestAccountSnapshot.Merge([renamed, added], new Dictionary<string, byte[]> { ["b.bin"] = [9], ["c.bin"] = [3] }, existing, DateTimeOffset.Now);

        Assert.Equal(["renamed", "new", "removed by accident"], merged.Accounts.Select(a => a.DisplayName));
        Assert.All(merged.Accounts, a => Assert.False(a.IsConnected));
        Assert.Equal([1], merged.Credentials["a.bin"]);
        Assert.Equal([9], merged.Credentials["b.bin"]);
        Assert.Equal([3], merged.Credentials["c.bin"]);
        Assert.Equal([kept.Id], merged.Missing([renamed.Id, added.Id]).Select(a => a.Id));
    }

    [Fact]
    public void ASnapshotRoundTripsThroughJson()
    {
        var snapshot = TestAccountSnapshot.Merge([Account("one")], new Dictionary<string, byte[]> { ["x.bin"] = [1, 2, 3] }, null, DateTimeOffset.UnixEpoch);
        var text = System.Text.Json.JsonSerializer.Serialize(snapshot, TestAccountSnapshot.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<TestAccountSnapshot>(text, TestAccountSnapshot.Options)!;

        Assert.Equal(snapshot.Accounts.Single().Id, back.Accounts.Single().Id);
        Assert.Equal([1, 2, 3], back.Credentials["x.bin"]);
    }

    [Fact]
    public void ADebugFolderStartsOnceFromTheReleaseOneWhichIsOnlyRead()
    {
        var release = Directory.CreateDirectory(Path.Combine(_root, "FileFluss")).FullName;
        File.WriteAllText(Path.Combine(release, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(release, "accounts.json"), "[]");
        File.WriteAllText(Path.Combine(release, "index.sqlite"), "big");
        Directory.CreateDirectory(Path.Combine(release, "credentials"));
        File.WriteAllBytes(Path.Combine(release, "credentials", "abc.bin"), [7]);
        var before = Directory.GetFiles(release, "*", SearchOption.AllDirectories).Order().ToList();
        var debug = Path.Combine(_root, "FileFluss-Debug");

        Assert.True(TestAccountSnapshot.SeedDebugFolder(release, debug));

        Assert.True(File.Exists(Path.Combine(debug, "settings.json")));
        Assert.True(File.Exists(Path.Combine(debug, "accounts.json")));
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(debug, "credentials", "abc.bin")));
        Assert.False(File.Exists(Path.Combine(debug, "index.sqlite")), "the index rebuilds; it isn't copied");
        Assert.Equal(before, Directory.GetFiles(release, "*", SearchOption.AllDirectories).Order().ToList());

        // Once only: a debug folder that exists is the debug build's own.
        File.WriteAllText(Path.Combine(debug, "accounts.json"), "[\"debug's own\"]");
        Assert.False(TestAccountSnapshot.SeedDebugFolder(release, debug));
        Assert.Equal("[\"debug's own\"]", File.ReadAllText(Path.Combine(debug, "accounts.json")));
    }

    [Fact]
    public void NothingToStartFromLeavesNoFolder()
    {
        var debug = Path.Combine(_root, "FileFluss-Debug");
        Assert.False(TestAccountSnapshot.SeedDebugFolder(Path.Combine(_root, "absent"), debug));
        Assert.False(Directory.Exists(debug));
    }

#if DEBUG
    [Fact]
    public void ADebugBuildKeepsItsOwnDataFolder()
    {
        Assert.NotEqual(SettingsStore.ReleaseDataDirectory, SettingsStore.DataDirectory);
        Assert.EndsWith("FileFluss-Debug", SettingsStore.DataDirectory, StringComparison.Ordinal);
    }
#endif
}
