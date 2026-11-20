// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.App.Services;
using FileFluss.Core.Models;
using FileFluss.Core.Security;
using FileFluss.Core.Services;

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
