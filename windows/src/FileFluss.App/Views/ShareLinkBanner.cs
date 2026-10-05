// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.Core.Models;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// The confirmation shown at the bottom of a panel after a share link was copied — the macOS
/// <c>shareLinkBanner</c>. A confirmation, not a dialog: it goes away on its own after six
/// seconds, and the link is already on the clipboard.
/// </summary>
public static class ShareLinkBanner
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    private static readonly ConditionalWeakTable<UIElement, OverlayAdorner> Shown = [];

    public static void Show(UIElement panel, CloudShareLink link, Uri shown)
    {
        if (Shown.TryGetValue(panel, out var previous))
        {
            previous.Remove();
            Shown.Remove(panel);
        }

        OverlayAdorner? adorner = null;
        var stack = new StackPanel();

        // A link that isn't public looks exactly like one that is until the recipient hits a
        // sign-in page — so say it here, not only in the small print below.
        var icon = new TextBlock
        {
            Text = link.IsPublic ? Glyphs.Link : Glyphs.Warning,
            FontFamily = new FontFamily(Glyphs.FontFamily),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        if (link.IsPublic)
        {
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        }
        else
        {
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0x93, 0x0E));
        }

        var close = new Button
        {
            Content = Glyphs.Cancel,
            FontFamily = new FontFamily(Glyphs.FontFamily),
            FontSize = 10,
            Padding = new Thickness(4),
            Margin = new Thickness(12, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L("Close"),
        };
        close.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        System.Windows.Automation.AutomationProperties.SetName(close, L("Close"));
        close.Click += (_, _) => Dismiss(panel, adorner);

        var title = new TextBlock
        {
            Text = link.IsPublic ? L("Share link copied") : L("Link copied — not public"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(icon);
        header.Children.Add(close);
        header.Children.Add(title);
        stack.Children.Add(header);

        stack.Children.Add(Secondary(new TextBlock
        {
            Text = shown.ToString(),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = shown.ToString(),
        }));
        if (link.ExpiresAt is { } expires)
        {
            stack.Children.Add(Secondary(new TextBlock { Text = L("Expires {0}", expires.ToLocalTime().ToString("g", Culture)), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) }));
        }

        // Where the server granted less than asked for, or the link behaves unusually, the
        // provider's note explains it.
        if (link.Note is { } note)
        {
            stack.Children.Add(Secondary(new TextBlock { Text = note, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }));
        }

        var card = OverlayAdorner.Card(new Border { Padding = new Thickness(14, 10, 14, 10), Child = stack });
        card.MaxWidth = 420;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Bottom;
        card.Margin = new Thickness(16, 0, 16, 36);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(card, System.Windows.Automation.AutomationLiveSetting.Polite);
        System.Windows.Automation.AutomationProperties.SetName(card, title.Text);

        adorner = OverlayAdorner.Show(panel, card);
        if (adorner is null)
        {
            return;
        }

        Shown.AddOrUpdate(panel, adorner);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = Lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss(panel, adorner);
        };
        timer.Start();
    }

    private static void Dismiss(UIElement panel, OverlayAdorner? adorner)
    {
        if (adorner is null)
        {
            return;
        }

        adorner.Remove();
        if (Shown.TryGetValue(panel, out var current) && ReferenceEquals(current, adorner))
        {
            Shown.Remove(panel);
        }
    }

    private static TextBlock Secondary(TextBlock block)
    {
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }
}
