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
    /// <param name="cancellationToken">The panel's "Cancel Sign-In": ends a browser sign-in that is still waiting.</param>
    public static async Task RunAsync(Window? owner, AppServices services, PanelViewModel panel, CancellationToken cancellationToken = default)
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
        catch (Exception e)
        {
            // Its stored settings couldn't be read back.
            MessageDialog.Error(owner, L("Sign In Again"), e.Message);
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
            var result = await provider.ConnectAsync(ConnectValues.Empty(provider.ConnectSpec.Modes[0].Id), SilentConnectPrompt.Instance, cancellationToken);
            if (!services.Accounts.Update(account.Id, provider, result))
            {
                // The account was removed while signing in.
                provider.Dispose();
                return;
            }

            await panel.ReloadAfterReauthenticationAsync();
        }
        catch (Exception e) when (e is OperationCanceledException || e is CloudProviderException { Kind: CloudErrorKind.Cancelled })
        {
            // Cancelled from the panel: nothing to report, the panel still offers Sign In Again.
            provider.Dispose();
        }
        catch (Exception e)
        {
            provider.Dispose();
            MessageDialog.Error(owner, L("Sign In Again"), e.Message);
        }
    }
}
