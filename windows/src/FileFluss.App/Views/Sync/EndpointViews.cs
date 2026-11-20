// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;
using FileFluss.Core.Search;
using FileFluss.Core.Sync;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Sync;

/// <summary>
/// A panel's folder captured as one side of a sync or a comparison, with what the endpoint
/// card shows about it. <see cref="Endpoint"/> is null when the panel shows no readable
/// folder (a disconnected account); "This PC" gives an endpoint that is not
/// <see cref="SyncEndpoint.IsUsable"/>.
/// </summary>
internal sealed record PanelSnapshot(PanelSide Side, PanelLocation Location, SyncEndpoint? Endpoint, CloudAccount? Account, bool IsReadOnly)
{
    public static PanelSnapshot Of(PanelViewModel panel)
    {
        var account = panel.Account;
        SyncEndpoint? endpoint = null;
        if (panel.Provider is { } provider)
        {
            endpoint = new SyncEndpoint(provider, panel.CurrentPath)
            {
                DisplayName = account?.DisplayName,
                IsOffline = provider is OfflineIndexProvider || account is { IsOfflineMode: true },
            };
        }

        return new PanelSnapshot(panel.Side, panel.Location, endpoint, account, panel.IsReadOnly);
    }

    /// <summary>A usable folder: what the card shows instead of "No folder open".</summary>
    public SyncEndpoint? UsableEndpoint => Endpoint is { IsUsable: true } endpoint ? endpoint : null;

    /// <summary>The panel location of <paramref name="relativePath"/> ("a/b") beneath this side's folder.</summary>
    public PanelLocation LocationOf(string relativePath)
        => Location with { Path = Endpoint is { } endpoint ? endpoint.Resolve(relativePath) : Location.Path };

    /// <summary>How confirmations name this side: the account, or the folder on this PC.</summary>
    public string PlaceName => Account?.DisplayName ?? Endpoint?.FolderName ?? string.Empty;
}

/// <summary>
/// The "Left" / "Right" card at the top of the Compare and Sync windows: the account (logo and
/// name) for a cloud folder, then the folder's path — or "No folder open".
/// </summary>
internal sealed class EndpointCard : Border
{
    private readonly TextBlock _caption = new() { FontSize = 12 };
    private readonly StackPanel _accountRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Image _logo = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _account = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel _pathRow = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40 };
    private readonly TextBlock _empty = new() { FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0) };

    public EndpointCard()
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 9, 12, 10);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        _accountRow.Children.Add(_logo);
        _accountRow.Children.Add(_account);
        var folder = new Image { Width = 16, Height = 16, Source = IconCache.Folder(), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(folder, Dock.Left);
        _pathRow.Children.Add(folder);
        _pathRow.Children.Add(_path);

        Child = new StackPanel { Children = { _caption, _accountRow, _pathRow, _empty } };
    }

    public void Show(string title, PanelSnapshot? side)
    {
        _caption.Text = title;
        var endpoint = side?.UsableEndpoint;
        var account = endpoint is { IsCloud: true } ? side!.Account : null;
        _accountRow.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        if (account is not null)
        {
            _logo.Source = IconCache.Logo(account.ProviderType);
            _account.Text = account.DisplayName;
        }

        _pathRow.Visibility = endpoint is null ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = endpoint is null ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = L("No folder open");
        _path.Text = endpoint?.DisplayPath ?? string.Empty;
        ToolTip = endpoint?.DisplayPath;

        var spoken = endpoint is null ? _empty.Text : account is null ? _path.Text : $"{account.DisplayName}, {_path.Text}";
        AutomationProperties.SetName(this, $"{title}: {spoken}");
    }
}

/// <summary>Small builders shared by the Compare and Sync windows.</summary>
internal static class WindowKit
{
    public static Border Divider(Thickness margin)
    {
        var line = new Border { Height = 1, Margin = margin };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    public static TextBlock Secondary(string text = "", double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    public static TextBlock Glyph(string glyph, double size = 14) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(Glyphs.FontFamily),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A button with a glyph before its caption.</summary>
    public static Button GlyphButton(string glyph, string caption, string automationId)
    {
        var button = new Button { Padding = new Thickness(10, 4, 12, 5) };
        SetGlyphCaption(button, glyph, caption);
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    public static void SetGlyphCaption(Button button, string glyph, string caption)
    {
        var icon = Glyph(glyph, 13);
        icon.Margin = new Thickness(0, 1, 7, 0);
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center } } };
        AutomationProperties.SetName(button, caption);
    }

    public static void Accent(Button button)
    {
        if (Application.Current?.TryFindResource("AccentButtonStyle") is Style style)
        {
            button.Style = style;
        }
    }
}
