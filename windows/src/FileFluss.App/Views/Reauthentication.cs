// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Accounts;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Providers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// "Sign In Again" from a panel whose credentials stopped working. Browser-only providers
/// (Google, Dropbox, Box, OneDrive) re-run their OAuth flow right here; the rest open the
/// account's edit form, since they need something typed.
/// </summary>
public static class Reauthentication
{
    public static async Task RunAsync(Window? owner, AppServices services, PanelViewModel panel)
    {
        if (panel.Account is not { } account)
        {
            return;
        }

        CloudProvider provider;
        try
        {
            provider = services.Accounts.CreateProvider(account.ProviderType, account.Id);
        }
        catch (NotSupportedException)
        {
            MessageDialog.Error(owner, L("Sign In Again"), L("This account type isn't available in this version."));
            return;
        }

        if (!provider.ConnectSpec.SupportsInlineReauthentication)
        {
            provider.Dispose();
            if (AccountDialog.Edit(owner, services, account.Id))
            {
                await panel.ReloadAfterReauthenticationAsync();
            }

            return;
        }

        try
        {
            var result = await provider.ConnectAsync(ConnectValues.Empty(provider.ConnectSpec.Modes[0].Id), SilentConnectPrompt.Instance);
            services.Accounts.Update(account.Id, provider, result);
            await panel.ReloadAfterReauthenticationAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            provider.Dispose();
            MessageDialog.Error(owner, L("Sign In Again"), e.Message);
        }
    }
}
