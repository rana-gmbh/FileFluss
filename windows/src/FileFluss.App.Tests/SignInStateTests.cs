// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.ICloud;
using FileFluss.Core.Security;
using FileFluss.Core.Services;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Tests;

/// <summary>
/// macOS 1.6 "an expired sign-in keeps the panel usable": a failed sign-in is remembered for
/// the session apart from <see cref="CloudAccount.IsConnected"/>, so the panel stays writable
/// and the account comes back by itself once a listing works again.
/// </summary>
public sealed class SignInStateTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ff-signin-").FullName;

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
    public void AnExpiredSignInLeavesTheAccountConnectedAndUnsaved()
    {
        var (service, id, path) = ServiceWithOneAccount();
        var stored = File.ReadAllText(path);
        var changes = new List<Guid>();
        service.SignInChanged += (_, changed) => changes.Add(changed);

        service.MarkNeedsReauthentication(id);

        var account = service.Find(id)!;
        Assert.True(account.IsConnected, "the panel must not turn read-only or lose its menus");
        Assert.False(account.IsEffectivelyOffline);
        Assert.True(service.NeedsSignIn(id));
        Assert.False(service.IsSignedIn(account));
        Assert.Equal([id], changes);
        Assert.Equal(stored, File.ReadAllText(path));

        // Reported again by the other panel: one change, not two.
        service.MarkNeedsReauthentication(id);
        Assert.Single(changes);
    }

    [Fact]
    public void AListingThatWorksClearsIt()
    {
        var (service, id, _) = ServiceWithOneAccount();
        service.MarkNeedsReauthentication(id);

        service.MarkSignInWorks(id);

        Assert.False(service.NeedsSignIn(id));
        Assert.True(service.IsSignedIn(service.Find(id)!));
    }

    // MARK: - Reconnecting (macOS a18ca97)

    /// <summary>The report that started it on macOS: "Cloud account not connected" with only Retry, which could never work.</summary>
    [Fact]
    public async Task APanelWithoutAUsableSignInAsksForOne()
    {
        var (services, id) = ServicesWithOneAccount(CloudProviderType.WebDav);
        var panel = new PanelViewModel(PanelSide.Left, services);

        await panel.NavigateAsync(PanelLocation.Cloud(id, "/"));

        Assert.True(panel.NeedsReauthentication);
        Assert.Equal(L("FileFluss couldn't use the saved sign-in for this account. Reconnect it to see your files again."), panel.Error);
        Assert.Equal(L("Reconnect…"), panel.ReconnectButtonText);
        Assert.Equal(L("Enter your {0} sign-in details again. Nothing else about the account changes.", "WebDAV"), panel.ReconnectCaption);
        Assert.True(panel.ShowsReconnectButtons);
        Assert.False(panel.ShowsRetry);
        Assert.False(panel.IsWaitingForBrowserSignIn);
        Assert.False(panel.IsLoading);
        Assert.Empty(panel.Rows);
        Assert.True(services.Accounts.NeedsSignIn(id));
        Assert.Null(services.Hub.Get(id));
    }

    /// <summary>Credentials that couldn't be read at launch (a locked file, a profile still loading) come back by themselves.</summary>
    [Fact]
    public async Task ASignInThatCanBeReadAgainBringsThePanelBack()
    {
        var drive = Directory.CreateDirectory(Path.Combine(_folder, "iCloudDrive")).FullName;
        File.WriteAllText(Path.Combine(drive, "hello.txt"), "hi");
        var (services, id) = ServicesWithOneAccount(CloudProviderType.ICloud);
        services.Credentials.Save($"icloud.{id}", new Dictionary<string, string> { ["rootPath"] = drive });
        var panel = new PanelViewModel(PanelSide.Left, services);

        await panel.NavigateAsync(PanelLocation.Cloud(id, "/"));

        Assert.Null(panel.Error);
        Assert.False(panel.NeedsReauthentication);
        Assert.Equal(["hello.txt"], panel.Rows.Select(r => r.Name));
        Assert.IsType<ICloudProvider>(services.Hub.Get(id));
        Assert.True(services.Accounts.IsSignedIn(services.Accounts.Find(id)!));
    }

    /// <summary>Try Again beside the way back reads the saved sign-in afresh: it may only have been unreadable for a moment.</summary>
    [Fact]
    public async Task TryAgainReadsTheSavedSignInAfresh()
    {
        if (ICloudDriveLocator.FindRoot() is not null)
        {
            // A PC with iCloud for Windows has a real iCloud Drive for the account to fall back on.
            return;
        }

        var drive = Directory.CreateDirectory(Path.Combine(_folder, "iCloudDrive")).FullName;
        File.WriteAllText(Path.Combine(drive, "hello.txt"), "hi");
        var (services, id) = ServicesWithOneAccount(CloudProviderType.ICloud);
        var panel = new PanelViewModel(PanelSide.Left, services);
        await panel.NavigateAsync(PanelLocation.Cloud(id, "/"));

        Assert.True(panel.NeedsReauthentication);
        Assert.Equal(L("iCloud Drive isn't available. Check that this Mac is signed in to iCloud with iCloud Drive turned on."), panel.Error);
        Assert.Equal(L("Open iCloud Settings…"), panel.ReconnectButtonText);
        Assert.Empty(panel.ReconnectCaption);

        services.Credentials.Save($"icloud.{id}", new Dictionary<string, string> { ["rootPath"] = drive });
        await panel.RetryAsync();

        Assert.False(panel.NeedsReauthentication);
        Assert.False(services.Accounts.NeedsSignIn(id));
        Assert.Equal(["hello.txt"], panel.Rows.Select(r => r.Name));
    }

    /// <summary>macOS reloadCloudPanels: not only the panel whose banner or form signed the account in again.</summary>
    [Fact]
    public async Task EveryPanelShowingTheAccountListsAgainAfterANewSignIn()
    {
        var drive = Directory.CreateDirectory(Path.Combine(_folder, "iCloudDrive")).FullName;
        File.WriteAllText(Path.Combine(drive, "a.txt"), "a");
        var (services, id) = ServicesWithOneAccount(CloudProviderType.ICloud);
        services.Credentials.Save($"icloud.{id}", new Dictionary<string, string> { ["rootPath"] = drive });
        var left = new PanelViewModel(PanelSide.Left, services);
        var right = new PanelViewModel(PanelSide.Right, services);
        await left.NavigateAsync(PanelLocation.Cloud(id, "/"));
        await right.NavigateAsync(PanelLocation.Cloud(id, "/"));
        File.WriteAllText(Path.Combine(drive, "b.txt"), "b");
        var listed = new[] { left, right }.Select(panel =>
        {
            var done = new TaskCompletionSource();
            panel.Loaded += (_, _) => done.TrySetResult();
            return done.Task;
        }).ToList();

        Assert.True(services.Accounts.Update(id, new ICloudProvider(id, services.Credentials), new ConnectResult(null)));

        await Task.WhenAll(listed).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(left.RowNamed("b.txt"));
        Assert.NotNull(right.RowNamed("b.txt"));
    }

    /// <summary>A saved account that hasn't been restored yet, as after a launch that couldn't read its credentials.</summary>
    private (AppServices Services, Guid Id) ServicesWithOneAccount(CloudProviderType type)
    {
        var data = Directory.CreateDirectory(Path.Combine(_folder, "data-" + Guid.NewGuid().ToString("N"))).FullName;
        var id = Guid.NewGuid();
        new AccountStore(Path.Combine(data, "accounts.json")).Save([new CloudAccount { Id = id, ProviderType = type, DisplayName = type.DisplayName(), IsConnected = true }]);
        return (new AppServices(data), id);
    }

    private (AccountsService Service, Guid Id, string Path) ServiceWithOneAccount()
    {
        var path = Path.Combine(_folder, "accounts.json");
        var id = Guid.NewGuid();
        var store = new AccountStore(path);
        store.Save([new CloudAccount { Id = id, ProviderType = CloudProviderType.WebDav, DisplayName = "WebDAV", IsConnected = true }]);
        var service = new AccountsService(new AccountStore(path), new ProviderHub(), new MemoryCredentialStore());

        // Loaded accounts start disconnected until their provider is restored; this one has been.
        service.Update(id, new Core.Providers.WebDav.WebDavProvider(id, new MemoryCredentialStore()), new Core.Providers.ConnectResult(null));
        return (service, id, path);
    }
}
