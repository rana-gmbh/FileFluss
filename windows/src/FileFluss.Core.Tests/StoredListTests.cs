// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Nodes;
using FileFluss.Core.Models;
using FileFluss.Core.Services;
using FileFluss.Core.Settings;

namespace FileFluss.Core.Tests;

/// <summary>
/// macOS 1.6 "your account list and favourites are safe from a damaged entry": one entry
/// this version can't read is kept aside and written back, and never costs the others.
/// </summary>
public sealed class StoredListTests : IDisposable
{
    // An account for a provider this version doesn't know, as a newer version would write it.
    private const string FutureAccount = """{"id":"6f1c3a2e-5b1d-4c7e-9a10-2b3c4d5e6f70","providerType":"someFutureCloud","displayName":"Future","isConnected":true}""";

    // A favourite whose id isn't a GUID: damaged.
    private const string DamagedFavorite = """{"id":"not-a-guid","displayName":"Broken","icon":"folder"}""";

    private readonly string _root = Directory.CreateTempSubdirectory("ff-storedlist-").FullName;

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

    [Fact]
    public void AnAccountThisVersionCantReadDoesntCostTheOthers()
    {
        var path = Path.Combine(_root, "accounts.json");
        var good = new CloudAccount { ProviderType = CloudProviderType.Dropbox, DisplayName = "Dropbox" };
        new AccountStore(path).Save([good]);
        var stored = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        stored.Insert(0, JsonNode.Parse(FutureAccount));
        File.WriteAllText(path, stored.ToJsonString());

        var store = new AccountStore(path);
        var accounts = store.Load();

        Assert.Equal([good.Id], accounts.Select(a => a.Id));
        Assert.False(File.Exists(path + ".bak"), "the main file was readable: no fallback to an older copy");

        // Saving — after adding one — writes the unreadable entry back unchanged.
        var added = new CloudAccount { ProviderType = CloudProviderType.WebDav, DisplayName = "WebDAV" };
        store.Save([.. accounts, added]);
        var written = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        Assert.Equal(3, written.Count);
        Assert.Contains(written, e => e!["providerType"]!.GetValue<string>() == "someFutureCloud");
        Assert.Equal(2, new AccountStore(path).Load().Count);
    }

    [Fact]
    public void AListOfOnlyUnreadableAccountsIsNeverSavedAway()
    {
        var path = Path.Combine(_root, "accounts.json");
        File.WriteAllText(path, $"[{FutureAccount}]");

        var store = new AccountStore(path);
        Assert.Empty(store.Load());
        store.Save([]);

        Assert.Single(JsonNode.Parse(File.ReadAllText(path))!.AsArray());
    }

    [Fact]
    public void ADamagedFavouriteKeepsTheOtherFavouritesAndEverySetting()
    {
        var path = Path.Combine(_root, "settings.json");
        var first = new SettingsStore(path);
        var kept = SidebarFavorite.ForLocal("Projects", "folder", @"C:\Projects");
        first.Settings.LeftFavorites = [kept];
        first.Settings.RightFavorites = [SidebarFavorite.ForLocal("Music", "music", @"C:\Music")];
        first.Settings.ShowHiddenFiles = true;
        first.Save();

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["sidebarFavorites.left"]!.AsArray().Add(JsonNode.Parse(DamagedFavorite));
        File.WriteAllText(path, root.ToJsonString());

        var second = new SettingsStore(path);
        Assert.True(second.Settings.ShowHiddenFiles, "one bad favourite must not reset the other settings");
        Assert.Equal([kept.Id], second.Settings.LeftFavorites!.Select(f => f.Id));
        Assert.Single(second.Settings.RightFavorites!);

        // A change and a save keep the damaged entry for whoever can read it.
        second.Settings.LeftFavorites = [.. second.Settings.LeftFavorites!, SidebarFavorite.ForLocal("Downloads", "download", @"C:\Downloads")];
        second.Save();
        var left = JsonNode.Parse(File.ReadAllText(path))!["sidebarFavorites.left"]!.AsArray();
        Assert.Equal(3, left.Count);
        Assert.Contains(left, e => e!["id"]!.GetValue<string>() == "not-a-guid");
        Assert.Equal(2, new SettingsStore(path).Settings.LeftFavorites!.Count);
    }
}
