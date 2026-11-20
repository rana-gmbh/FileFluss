// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Accounts;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;

namespace FileFluss.App.Views;

/// <summary>
/// Reconnecting an account whose sign-in stopped working, from its panel's Sign-In Required
/// banner or the sidebar (macOS <c>AppState.reconnect</c>). Each kind of account has its own
/// way back (<see cref="ReconnectMethod"/>): the provider's page in the browser, the account's
/// form opened to reconnect it, iCloud for Windows, or Settings for an account that has to be
/// removed and added again.
/// </summary>
public static class Reauthentication
{
    /// <summary>
    /// iCloud for Windows' application user model id, as the Microsoft Store installs it, and
    /// its Store listing for when it isn't installed.
    /// </summary>
    private const string ICloudAppId = @"shell:AppsFolder\AppleInc.iCloud_nzyj5cx40ttqa!iCloud";

    private const string ICloudStorePage = "ms-windows-store://pdp/?ProductId=9PKTQ5699M62";

    public static void Reconnect(Window? owner, MainViewModel main, Guid accountId)
    {
        var services = main.Services;
        if (services.Accounts.Find(accountId) is not { } account)
        {
            return;
        }

        switch (account.ProviderType.ReconnectMethod())
        {
            case ReconnectMethod.Browser:
                // A provider without a browser sign-in that needs nothing typed gets its form.
                if (!services.Accounts.ReconnectInBrowser(accountId))
                {
                    AccountDialog.Reconnect(owner, services, accountId);
                }

                break;
            case ReconnectMethod.Credentials:
                AccountDialog.Reconnect(owner, services, accountId);
                break;
            case ReconnectMethod.SystemSettings:
                OpenICloudForWindows();
                break;
            case ReconnectMethod.AddAgain:
                main.Shell.ShowSettings("cloud");
                break;
        }
    }

    /// <summary>
    /// iCloud for Windows, where its sign-in and iCloud Drive are switched on — the macOS
    /// Apple Account pane. Its Microsoft Store page when it can't be started, which also offers
    /// to install it.
    /// </summary>
    private static void OpenICloudForWindows()
    {
        foreach (var target in new[] { ICloudAppId, ICloudStorePage })
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                return;
            }
            catch (Win32Exception e)
            {
                SupportLogger.Shared.Log($"Opening iCloud for Windows via {target} failed: {e.Message}", "Accounts", LogLevel.Notice);
            }
        }
    }
}
