// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.App.Services;
using FileFluss.Core.Commands;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Services;
using FileFluss.Core.Settings;
using FileFluss.Native;

namespace FileFluss.App;

/// <summary>
/// The composition root: one instance of each long-lived service, created once at startup.
/// The macOS app reached the same objects through <c>AppState</c> and a handful of
/// <c>.shared</c> singletons.
/// </summary>
public sealed class AppServices
{
    public AppServices(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        SettingsStore = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
        Credentials = new DpapiCredentialStore(Path.Combine(dataDirectory, "credentials"));
        Hub = new ProviderHub();
        Accounts = new AccountsService(new AccountStore(Path.Combine(dataDirectory, "accounts.json")), Hub, Credentials);
        Transfers = new TransferCenter();
        Keyboard = new KeyboardMap(Settings);
        Search = new SearchServices(Path.Combine(dataDirectory, "index.sqlite"), Hub);

        // Win32 implementations of the operations Core leaves pluggable.
        LocalFileProvider.CopyFileHandler = ShellFileOperations.CopyFileAsync;
        LocalFileProvider.RecycleHandler = ShellFileOperations.MoveToRecycleBin;
        StagingLocation.CustomFolder = Settings.CustomCacheFolder;
        SettingsStore.Changed += (_, name) =>
        {
            if (name is null or nameof(AppSettings.CustomCacheFolder))
            {
                StagingLocation.CustomFolder = Settings.CustomCacheFolder;
            }
        };
    }

    public static AppServices Current { get; set; } = null!;

    public string DataDirectory { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings => SettingsStore.Settings;

    public DpapiCredentialStore Credentials { get; }

    public ProviderHub Hub { get; }

    public AccountsService Accounts { get; }

    public TransferCenter Transfers { get; }

    public KeyboardMap Keyboard { get; }

    public SearchServices Search { get; }

    /// <summary>
    /// What a panel lists for a location: the disk, a connected account — or, for an account in
    /// offline mode or an unplugged drive, the read-only view of its index.
    /// </summary>
    public FileProvider? ProviderFor(PanelLocation location)
    {
        var account = location.AccountId is { } id ? Accounts.Find(id) : null;
        return Search.OfflineProvider(location, account) ?? Hub.For(location);
    }
}
