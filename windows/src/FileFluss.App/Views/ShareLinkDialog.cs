// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// Share Link: asks the provider for a public link, verifies it the way a stranger would,
/// and puts it on the clipboard. Offers password, expiry and download options only where
/// the account can honour them, and says up front when they need a paid plan.
/// </summary>
public sealed class ShareLinkDialog : Window
{
    private static readonly string[] PlanRefusal = ["forbidden", "not_authorized", "not authorized", "settings_error", "403", "payment", "upgrade", "premium", "plan", "subscription"];

    private readonly AppServices _services;
    private readonly CloudProvider _provider;
    private readonly CloudFileItem _item;
    private readonly ShareLinkCapabilities _capabilities;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 20, 24, 20) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _linkBox = new() { IsReadOnly = true, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly CheckBox _usePassword = new() { Content = L("Protect with password"), Margin = new Thickness(0, 10, 0, 4) };
    private readonly PasswordBox _password = new() { IsEnabled = false };
    private readonly CheckBox _useExpiry = new() { Content = L("Set an expiry date"), Margin = new Thickness(0, 10, 0, 4) };
    private readonly DatePicker _expiry = new() { IsEnabled = false, SelectedDate = DateTime.Today.AddDays(7) };
    private readonly CheckBox _allowDownload = new() { Content = L("Allow downloading"), IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _create = new() { Content = L("Create Link"), MinWidth = 110, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _stop = new() { Content = L("Stop Sharing"), Visibility = Visibility.Collapsed };
    private CloudShareLink? _existing;

    private ShareLinkDialog(AppServices services, CloudProvider provider, CloudFileItem item)
    {
        _services = services;
        _provider = provider;
        _item = item;
        _capabilities = provider.ShareLinkCapabilities;
        Title = L("Share Link…").TrimEnd('…');
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _body.Children.Add(new TextBlock { Text = item.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _body.Children.Add(_status);
        _body.Children.Add(_linkBox);
        var flags = _services.Settings;
        var rejectedPassword = flags.GetFlag($"shareOptionRejected.password.{provider.AccountId}");
        var rejectedExpiry = flags.GetFlag($"shareOptionRejected.expiry.{provider.AccountId}");
        if (_capabilities.SupportsPassword && !rejectedPassword)
        {
            _usePassword.Checked += (_, _) => _password.IsEnabled = true;
            _usePassword.Unchecked += (_, _) => _password.IsEnabled = false;
            _body.Children.Add(_usePassword);
            _body.Children.Add(_password);
        }

        if (_capabilities.SupportsExpiry && !rejectedExpiry)
        {
            _useExpiry.IsChecked = _capabilities.RequiresExpiry;
            _useExpiry.IsEnabled = !_capabilities.RequiresExpiry;
            _expiry.IsEnabled = _capabilities.RequiresExpiry;
            _useExpiry.Checked += (_, _) => _expiry.IsEnabled = true;
            _useExpiry.Unchecked += (_, _) => _expiry.IsEnabled = false;
            if (_capabilities.MaximumExpiry is { } max)
            {
                _expiry.DisplayDateEnd = DateTime.Today.Add(max);
                _expiry.SelectedDate = DateTime.Today.Add(max < TimeSpan.FromDays(7) ? max : TimeSpan.FromDays(7));
            }

            _expiry.DisplayDateStart = DateTime.Today.AddDays(1);
            _body.Children.Add(_useExpiry);
            _body.Children.Add(_expiry);
        }

        if (_capabilities.SupportsDownloadToggle)
        {
            _body.Children.Add(_allowDownload);
        }

        var paid = new List<string>();
        if (_capabilities.PasswordRequiresPaidPlan && _capabilities.SupportsPassword)
        {
            paid.Add(L("password protection"));
        }

        if (_capabilities.ExpiryRequiresPaidPlan && _capabilities.SupportsExpiry)
        {
            paid.Add(L("expiry dates"));
        }

        if (paid.Count > 0)
        {
            _body.Children.Add(new TextBlock
            {
                Text = L("{0} usually need a paid plan.", string.Join(L(" and "), paid)),
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        if (flags.GetFlag($"shareLinksNotPublic.{provider.AccountId}"))
        {
            var warning = new TextBlock { Text = L("Links from this account have needed a sign-in before. Check the link before you send it."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            _body.Children.Add(warning);
        }

        _body.Children.Add(_busy);
        var close = new Button { Content = L("Close"), MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent)
        {
            _create.Style = accent;
        }

        _create.Click += async (_, _) => await CreateAsync();
        _stop.Click += async (_, _) => await StopAsync();
        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_stop, Dock.Left);
        buttons.Children.Add(_stop);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { close, _create } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        _body.Children.Add(buttons);
        Content = _body;
        Loaded += async (_, _) => await LoadExistingAsync();
    }

    public static void Show(Window owner, AppServices services, PanelViewModel panel, CloudFileItem item)
    {
        if (panel.Provider is not CloudProvider provider || !provider.ShareLinkCapabilities.CanCreate)
        {
            return;
        }

        new ShareLinkDialog(services, provider, item) { Owner = owner }.ShowDialog();
    }

    private async Task LoadExistingAsync()
    {
        if (!_capabilities.CanQueryExisting)
        {
            _status.Text = L("Create a public link to this file and copy it to the clipboard.");
            return;
        }

        SetBusy(true);
        try
        {
            _existing = await _provider.ExistingShareLinkAsync(_item.Path);
        }
        catch (Exception)
        {
            _existing = null;
        }
        finally
        {
            SetBusy(false);
        }

        if (_existing is { } link)
        {
            ShowLink(link, L("This file is shared. The link is on the clipboard."), copy: true);
            _create.Content = _capabilities.CanUpdate ? L("Save Changes") : L("Copy Link");
            _stop.Visibility = _capabilities.CanRemove ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _status.Text = L("This file isn't shared with anyone who has the link.");
        }
    }

    private ShareLinkOptions Options() => new()
    {
        Password = _usePassword.IsChecked == true && _password.Password.Length > 0 ? _password.Password : null,
        Expiry = _useExpiry.IsChecked == true && _expiry.SelectedDate is { } date ? new DateTimeOffset(date.Date.AddDays(1).AddSeconds(-1)) : null,
        AllowDownload = _allowDownload.IsChecked != false,
    };

    private async Task CreateAsync()
    {
        var options = Options();
        SetBusy(true);
        try
        {
            CloudShareLink link;
            if (_existing is not null && _capabilities.CanUpdate)
            {
                link = await _provider.UpdateShareLinkAsync(_item.Path, options);
            }
            else if (_existing is not null && options == ShareLinkOptions.Default)
            {
                link = _existing;
            }
            else
            {
                link = await _provider.CreateShareLinkAsync(_item.Path, options);
            }

            NoteAccepted(options);
            link = await VerifyAsync(link);
            _existing = link;
            ShowLink(link, L("The link is on the clipboard."), copy: true);
            _stop.Visibility = _capabilities.CanRemove ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotAuthenticated)
        {
            _status.Text = CloudProviderException.Unauthorized().Message;
        }
        catch (Exception e)
        {
            NoteRejected(options, e.Message);
            _status.Text = L("Couldn't create the share link: {0}", e.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StopAsync()
    {
        if (!MessageDialog.Confirm(this, L("Stop Sharing"), L("People with the link will no longer be able to open \"{0}\".", _item.Name), L("Stop Sharing"), destructive: true))
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _provider.RemoveShareLinkAsync(_item.Path);
            _existing = null;
            _linkBox.Visibility = Visibility.Collapsed;
            _stop.Visibility = Visibility.Collapsed;
            _create.Content = L("Create Link");
            _status.Text = L("This file is no longer shared.");
        }
        catch (Exception e)
        {
            _status.Text = L("Couldn't change sharing: {0}", e.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Checks the link as a stranger; downgrades a refused direct link to the share page.</summary>
    private async Task<CloudShareLink> VerifyAsync(CloudShareLink link)
    {
        var accountKey = _provider.AccountId;
        if (link.DirectDownloadUrl is { } direct && direct != link.Url)
        {
            var blockedKey = $"shareLinkDirectBlocked.{accountKey}";
            if (_services.Settings.GetFlag(blockedKey))
            {
                return link with { DirectDownloadUrl = null, Note = Join(link.Note, L("This account can't serve direct download links, so the share page link was copied instead.")) };
            }

            var directCheck = await PublicUrlCheck.IsReachableAsync(direct);
            if (directCheck.CouldNotCheck || directCheck.Ok)
            {
                return link;
            }

            _services.Settings.SetFlag(blockedKey, true);
            return link with { DirectDownloadUrl = null, Note = Join(link.Note, L("This account can't serve direct download links, so the share page link was copied instead.")) };
        }

        var result = await PublicUrlCheck.IsReachableAsync(link.Url);
        var notPublicKey = $"shareLinksNotPublic.{accountKey}";
        if (result.Ok)
        {
            _services.Settings.SetFlag(notPublicKey, false);
            return link;
        }

        if (result.CouldNotCheck)
        {
            return link with { Note = Join(link.Note, L("The link couldn't be checked from here: {0}", result.Detail ?? L("the server didn't answer."))) };
        }

        _services.Settings.SetFlag(notPublicKey, true);
        return link with { IsPublic = false, Note = Join(link.Note, L("Checked just now: {0}", result.Detail ?? L("the link is not reachable without signing in."))) };
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;

    private void ShowLink(CloudShareLink link, string message, bool copy)
    {
        var url = (_services.Settings.ShareLinkPreferDirectDownload ? link.PreferredUrl : link.Url).ToString();
        _linkBox.Text = url;
        _linkBox.Visibility = Visibility.Visible;
        var text = message;
        if (!link.IsPublic)
        {
            text = L("This link only works for people who can sign in to the account.") + " " + (link.Note ?? string.Empty);
        }
        else if (link.Note is { } note)
        {
            text += " " + note;
        }

        if (link.ExpiresAt is { } expires)
        {
            text += " " + L("Expires") + ": " + expires.ToString("d", Culture);
        }

        _status.Text = text;
        if (copy)
        {
            try
            {
                Clipboard.SetText(url);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
        }
    }

    private void NoteAccepted(ShareLinkOptions options)
    {
        if (options.Password is not null)
        {
            _services.Settings.SetFlag($"shareOptionRejected.password.{_provider.AccountId}", false);
        }

        if (options.Expiry is not null)
        {
            _services.Settings.SetFlag($"shareOptionRejected.expiry.{_provider.AccountId}", false);
        }
    }

    /// <summary>A flat "forbidden" for a paid-only option means: stop offering it for this account.</summary>
    private void NoteRejected(ShareLinkOptions options, string message)
    {
        var text = message.ToLowerInvariant();
        if (!PlanRefusal.Any(text.Contains))
        {
            return;
        }

        if (options.Password is not null)
        {
            _services.Settings.SetFlag($"shareOptionRejected.password.{_provider.AccountId}", true);
        }

        if (options.Expiry is not null)
        {
            _services.Settings.SetFlag($"shareOptionRejected.expiry.{_provider.AccountId}", true);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _create.IsEnabled = !busy;
        _stop.IsEnabled = !busy;
    }
}
