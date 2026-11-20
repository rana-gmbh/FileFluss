// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.App.Services;
using FileFluss.App.Views.Accounts;
using FileFluss.Core.Commands;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// First-launch welcome. The macOS screen was about Full Disk Access, which Windows has no
/// counterpart for; here it introduces the two panels, offers the keyboard layout choice
/// (Explorer or Commander shortcuts) and a first cloud account.
/// </summary>
public sealed class WelcomeWindow : Window
{
    private readonly AppServices _services;

    private WelcomeWindow(Window owner, AppServices services)
    {
        _services = services;
        Owner = owner;
        Title = L("Welcome to FileFluss");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 16) };
        var icon = new Image { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        try
        {
            icon.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FileFluss.png")));
        }
        catch (Exception e) when (e is System.IO.IOException or NotSupportedException)
        {
        }

        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = L("Welcome to FileFluss"), FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(Secondary(L("Your files and your clouds, side by side."), 14, center: true, new Thickness(0, 4, 0, 18)));

        stack.Children.Add(Bullet(Glyphs.TwoPanes, L("Two panels"),
            L("Browse two places at once. F5 copies the selection to the other panel, F6 moves it, Tab switches panels.")));
        stack.Children.Add(Bullet(Glyphs.Cloud, L("Every cloud in one place"),
            L("Add OneDrive, Google Drive, Dropbox, pCloud, WebDAV, SFTP and many more. Copy between them as if they were folders.")));
        stack.Children.Add(Bullet(Glyphs.Search, L("Find anything"),
            L("Search local folders and every connected account at once. Index a drive or account to search it even while it is offline.")));

        stack.Children.Add(new TextBlock { Text = L("Keyboard shortcuts"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
        var presets = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };
        foreach (var preset in Enum.GetValues<KeyboardPreset>())
        {
            var radio = new RadioButton
            {
                Content = L(preset.DisplayName()),
                GroupName = "preset",
                IsChecked = services.Settings.KeyboardPreset == preset,
                Margin = new Thickness(0, 2, 0, 2),
            };
            radio.Checked += (_, _) => services.Settings.KeyboardPreset = preset;
            presets.Children.Add(radio);
        }

        stack.Children.Add(presets);
        stack.Children.Add(Secondary(L("You can change every shortcut later in Settings → Keyboard Map."), 12, center: false, new Thickness(2, 4, 0, 0)));

        var addAccount = new Button { Content = L("Add Cloud Account…"), Padding = new Thickness(14, 6, 14, 7), MinWidth = 120 };
        addAccount.Click += (_, _) =>
        {
            Finish();
            AccountDialog.Add(Owner, _services);
        };
        var start = new Button { Content = L("Get Started"), Padding = new Thickness(14, 6, 14, 7), MinWidth = 120, IsDefault = true, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.TryFindResource("AccentButtonStyle") is Style accent)
        {
            start.Style = accent;
        }

        AutomationProperties.SetName(start, L("Get Started"));
        start.Click += (_, _) => Finish();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 4) };
        buttons.Children.Add(addAccount);
        buttons.Children.Add(start);
        stack.Children.Add(buttons);

        Content = stack;
        Closed += (_, _) => _services.Settings.HasCompletedWelcome = true;
    }

    /// <summary>Shows the welcome screen once, on first launch (or after "Show Welcome Screen Again").</summary>
    public static void ShowIfNeeded(Window owner, AppServices services)
    {
        if (!services.Settings.HasCompletedWelcome)
        {
            Show(owner, services);
        }
    }

    public static void Show(Window owner, AppServices services)
    {
        if (owner.OwnedWindows.OfType<WelcomeWindow>().FirstOrDefault() is { } open)
        {
            open.Activate();
            return;
        }

        new WelcomeWindow(owner, services).Show();
    }

    private void Finish()
    {
        _services.Settings.HasCompletedWelcome = true;
        Close();
    }

    private static FrameworkElement Bullet(string glyph, string title, string text)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 20, Width = 30, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        body.Children.Add(Secondary(text, 12.5, center: false, new Thickness(0, 2, 0, 0)));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(body);
        return row;
    }

    private static TextBlock Secondary(string text, double size, bool center, Thickness margin)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
            TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
            HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }
}
