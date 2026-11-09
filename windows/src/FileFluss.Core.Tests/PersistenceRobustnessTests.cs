// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Services;
using FileFluss.Core.Settings;

namespace FileFluss.Core.Tests;

/// <summary>Settings and accounts survive damaged files, power cuts and a file locked at launch.</summary>
public sealed class PersistenceRobustnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-persist-" + Guid.NewGuid().ToString("N"));

    public PersistenceRobustnessTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CloudAccount Account(string name) => new() { ProviderType = CloudProviderType.Dropbox, DisplayName = name };

    [Fact]
    public void AccountSavesLeaveABackupAndNoTemporaryFiles()
    {
        var path = Path.Combine(_root, "accounts.json");
        var store = new AccountStore(path);

        store.Save([Account("one")]);
        store.Save([Account("one"), Account("two")]);

        Assert.True(File.Exists(path + ".bak"));
        Assert.Equal(["accounts.json", "accounts.json.bak"], Directory.GetFiles(_root).Select(Path.GetFileName).Order());
        Assert.Equal(2, new AccountStore(path).Load().Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0\0\0\0")]
    [InlineData("[{\"id\":")]
    public void ADamagedAccountFileFallsBackToThePreviousList(string damage)
    {
        var path = Path.Combine(_root, "accounts.json");
        var store = new AccountStore(path);
        store.Save([Account("kept")]);
        store.Save([Account("kept"), Account("newer")]);
        File.WriteAllText(path, damage);

        var reloaded = new AccountStore(path);
        var accounts = reloaded.Load();

        Assert.Equal(["kept"], accounts.Select(a => a.DisplayName));
        reloaded.Save([]);
        Assert.Equal(["kept"], new AccountStore(path).Load().Select(a => a.DisplayName));
    }

    [Fact]
    public void AnAccountFileLockedAtLaunchIsMergedNotOverwritten()
    {
        var path = Path.Combine(_root, "accounts.json");
        var existing = Account("existing");
        new AccountStore(path).Save([existing]);
        var store = new AccountStore(path);
        var added = Account("added this session");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(store.Load());

            // Still locked: refuse rather than replace the list with one that lacks "existing".
            store.Save([added]);
        }

        Assert.Equal(["existing"], new AccountStore(path).Load().Select(a => a.DisplayName));

        store.Save([added]);
        Assert.Equal(["added this session", "existing"], new AccountStore(path).Load().Select(a => a.DisplayName));
    }

    [Fact]
    public void ADamagedSettingsFileFallsBackToThePreviousOneAndIsKeptAside()
    {
        var path = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(path);
        store.Settings.ShowHiddenFiles = !store.Settings.ShowHiddenFiles;
        store.Flush(); // Saves are debounced; each Flush is one write.
        var wanted = store.Settings.ShowHiddenFiles;
        store.Settings.ConfirmDelete = !store.Settings.ConfirmDelete;
        store.Flush();
        File.WriteAllText(path, "\0\0\0\0");

        var reloaded = new SettingsStore(path);

        Assert.Equal(wanted, reloaded.Settings.ShowHiddenFiles);
        Assert.Contains(Directory.GetFiles(_root), f => f.Contains(".unreadable-", StringComparison.Ordinal));
    }

    [Fact]
    public void ASettingsFileLockedAtLaunchIsCopiedAsideBeforeDefaultsReplaceIt()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{\"ShowHiddenFiles\": true}");
        SettingsStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new SettingsStore(path);
        }

        store.Settings.ConfirmDelete = !store.Settings.ConfirmDelete;
        store.Flush();

        var aside = Assert.Single(Directory.GetFiles(_root), f => f.Contains(".unreadable-", StringComparison.Ordinal));
        Assert.Contains("ShowHiddenFiles", File.ReadAllText(aside), StringComparison.Ordinal);
    }
}
