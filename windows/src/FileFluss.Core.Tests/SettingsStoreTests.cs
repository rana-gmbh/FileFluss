// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Settings;

namespace FileFluss.Core.Tests;

/// <summary>Settings are written off the changing thread, shortly after a burst of changes, and on Flush.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("FileFluss-settings-").FullName;

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FlushWritesPendingChanges()
    {
        var store = new SettingsStore(SettingsPath);
        store.Settings.LeftSidebarWidth = 321;
        store.Settings.ShowStatusBar = false;
        store.Flush();

        var reloaded = new SettingsStore(SettingsPath);
        Assert.Equal(321, reloaded.Settings.LeftSidebarWidth);
        Assert.False(reloaded.Settings.ShowStatusBar);
    }

    [Fact]
    public async Task ABurstOfChangesIsWrittenOnceItSettles()
    {
        var store = new SettingsStore(SettingsPath);
        var changes = new List<string?>();
        store.Changed += (_, name) => changes.Add(name);
        for (var i = 0; i < 20; i++)
        {
            store.Settings.LeftSidebarWidth = 200 + i;
        }

        // Listeners hear every change at once; the file follows.
        Assert.Equal(20, changes.Count);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !(File.Exists(SettingsPath) && new SettingsStore(SettingsPath).Settings.LeftSidebarWidth == 219))
        {
            await Task.Delay(50);
        }

        Assert.Equal(219, new SettingsStore(SettingsPath).Settings.LeftSidebarWidth);
    }

    [Fact]
    public void BatchIsWrittenOnFlush()
    {
        var store = new SettingsStore(SettingsPath);
        string? announced = "none";
        store.Changed += (_, name) => announced = name;
        store.Batch(s =>
        {
            s.SinglePaneMode = true;
            s.RowSize = Models.FileListRowSize.Large;
        });

        Assert.Null(announced);
        store.Flush();
        var reloaded = new SettingsStore(SettingsPath);
        Assert.True(reloaded.Settings.SinglePaneMode);
        Assert.Equal(Models.FileListRowSize.Large, reloaded.Settings.RowSize);
    }
}
