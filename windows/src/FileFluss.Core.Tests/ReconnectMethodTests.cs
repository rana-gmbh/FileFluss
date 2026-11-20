// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Security;

namespace FileFluss.Core.Tests;

/// <summary>
/// Port of the macOS <c>ReconnectTests</c> mapping: every kind of account whose sign-in stopped
/// working has a way back, and the browser ones can take it without a form.
/// </summary>
public sealed class ReconnectMethodTests
{
    [Fact]
    public void EveryKindOfAccountHasAWayBackToSigningIn()
    {
        foreach (var type in new[] { CloudProviderType.GoogleDrive, CloudProviderType.Dropbox, CloudProviderType.Box, CloudProviderType.OneDrive, CloudProviderType.PCloud })
        {
            Assert.True(type.ReconnectMethod() == ReconnectMethod.Browser, type.ToString());
        }

        foreach (var type in new[]
                 {
                     CloudProviderType.WebDav, CloudProviderType.Sftp, CloudProviderType.Ftp, CloudProviderType.NextCloud, CloudProviderType.S3,
                     CloudProviderType.KDrive, CloudProviderType.GitHub, CloudProviderType.Jottacloud, CloudProviderType.TeraBox, CloudProviderType.Mega,
                 })
        {
            Assert.True(type.ReconnectMethod() == ReconnectMethod.Credentials, type.ToString());
        }

        Assert.Equal(ReconnectMethod.SystemSettings, CloudProviderType.ICloud.ReconnectMethod());
        Assert.Equal(ReconnectMethod.AddAgain, CloudProviderType.GoPro.ReconnectMethod());
        Assert.Equal(ReconnectMethod.AddAgain, CloudProviderType.GoogleDrivePicker.ReconnectMethod());
    }

    /// <summary>macOS opens the browser for Drime once it has a client ID; the Windows port has no browser sign-in for it.</summary>
    [Fact]
    public void DrimeTakesANewTokenInItsForm()
    {
        Assert.Equal(ReconnectMethod.Credentials, CloudProviderType.Drime.ReconnectMethod());
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Drime, Guid.NewGuid(), new MemoryCredentialStore());
        Assert.Null(provider.ConnectSpec.BrowserOnlyMode);
        Assert.Contains(provider.ConnectSpec.Modes.Single().Fields, f => f.Key == "token" && f.Required);
    }

    [Theory]
    [InlineData(CloudProviderType.GoogleDrive)]
    [InlineData(CloudProviderType.Dropbox)]
    [InlineData(CloudProviderType.Box)]
    [InlineData(CloudProviderType.OneDrive)]
    public void TheBrowserOnesSignInAgainWithoutAForm(CloudProviderType type)
    {
        using var provider = CloudProviderRegistry.Create(type, Guid.NewGuid(), new MemoryCredentialStore());
        var mode = provider.ConnectSpec.BrowserOnlyMode;
        Assert.NotNull(mode);
        Assert.True(mode.OpensBrowser);
    }

    [Fact]
    public void TheBrowserOnlyModeIsFoundAmongModesThatNeedTyping()
    {
        var password = new ConnectMode("password", "Password", [new ConnectField("password", ConnectFieldKind.Password, "Password")]);
        var browser = new ConnectMode("browser", "Browser", [ConnectField.Info("x")]) { OpensBrowser = true };
        var serverInBrowser = new ConnectMode("server", "Browser", [new ConnectField("url", ConnectFieldKind.Url, "Server")]) { OpensBrowser = true };

        Assert.Same(browser, new ConnectSpec([password, browser]).BrowserOnlyMode);
        Assert.False(new ConnectSpec([password, browser]).SupportsInlineReauthentication);

        // NextCloud's browser sign-in needs its server typed first: not one to run from a panel.
        Assert.Null(new ConnectSpec([serverInBrowser, password]).BrowserOnlyMode);
        Assert.Null(new ConnectSpec(new ConnectField("u", ConnectFieldKind.Text, "User")).BrowserOnlyMode);
    }
}
