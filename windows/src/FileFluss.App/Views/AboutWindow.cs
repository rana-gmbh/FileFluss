// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using FileFluss.Core.Net;
using FileFluss.Core.Settings;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>About FileFluss — the macOS About window's content.</summary>
public sealed class AboutWindow : Window
{
    private AboutWindow()
    {
        Title = L("About FileFluss");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 24), HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new Image { Width = 112, Height = 112, Margin = new Thickness(0, 0, 0, 10) };
        try
        {
            icon.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FileFluss.png")));
        }
        catch (Exception e) when (e is System.IO.IOException or NotSupportedException)
        {
        }

        stack.Children.Add(icon);
        stack.Children.Add(Centered(new TextBlock { Text = "FileFluss", FontSize = 26, FontWeight = FontWeights.SemiBold }));
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };
        versionRow.Children.Add(new TextBlock { Text = L("Version {0}", Version) + " · Windows", Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
        var releaseNotes = Link(L("Release Notes ↗"), ReleasesUrl);
        releaseNotes.FontSize = 11;
        releaseNotes.Margin = new Thickness(8, 0, 0, 0);
        releaseNotes.VerticalAlignment = VerticalAlignment.Center;
        versionRow.Children.Add(releaseNotes);
        stack.Children.Add(Centered(versionRow));
        stack.Children.Add(Centered(new TextBlock { Text = L("Made by Rana GmbH") }));
        stack.Children.Add(Centered(Link("www.filefluss.de", "https://www.filefluss.de")));
        stack.Children.Add(Centered(Link("github.com/rana-gmbh/filefluss", "https://github.com/rana-gmbh/filefluss")));
        stack.Children.Add(Centered(new TextBlock { Text = L("If you want to support this project,") + " " + L("please consider to "), Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }));
        stack.Children.Add(Centered(Link(L("Buy me a coffee ↗"), "https://buymeacoffee.com/robertrudolph")));
        stack.Children.Add(Centered(new TextBlock { Text = L("Released under the"), Margin = new Thickness(0, 12, 0, 0) }));
        stack.Children.Add(Centered(Link(L("GNU General Public License v3.0 ↗"), "https://www.gnu.org/licenses/gpl-3.0.html")));
        stack.Children.Add(Centered(new TextBlock
        {
            Text = L("App icon by @JohnnyFireOne · file-type icons by redbooth/free-file-icons (MIT)"),
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        }));
        stack.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 16) });
        stack.Children.Add(UpdateSection());
        Content = stack;
    }

    /// <summary>
    /// Windows releases are tagged <c>win-v*</c> next to the macOS ones; the release list shows
    /// both, newest first.
    /// </summary>
    public const string ReleasesUrl = "https://github.com/rana-gmbh/FileFluss/releases";

    /// <summary>macOS <c>updateSection</c>: "Check for Updates" and when the last check ran.</summary>
    private StackPanel UpdateSection()
    {
        var section = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var check = new Button { Content = L("Check for Updates"), MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Center };
        if (TryFindResource("AccentButtonStyle") is Style accent)
        {
            check.Style = accent;
        }

        check.Click += (_, _) => (Application.Current.MainWindow as MainWindow)?.RequestUpdateCheck(this);
        section.Children.Add(check);

        var lastChecked = new TextBlock { FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        void ShowLastChecked()
        {
            var last = AppServices.Current.Settings.LastUpdateCheck;
            lastChecked.Text = last is { } date ? L("Last checked: {0}", date.ToLocalTime().ToString("g", Culture)) : string.Empty;
            lastChecked.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        }

        ShowLastChecked();
        void OnSettingsChanged(object? sender, string? name)
        {
            if (name is null or nameof(AppSettings.LastUpdateCheck))
            {
                Dispatcher.BeginInvoke(ShowLastChecked);
            }
        }

        AppServices.Current.SettingsStore.Changed += OnSettingsChanged;
        Closed += (_, _) => AppServices.Current.SettingsStore.Changed -= OnSettingsChanged;
        section.Children.Add(lastChecked);
        return section;
    }

    public static string Version
        => typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0";

    public static void Show(Window owner) => new AboutWindow { Owner = owner }.ShowDialog();

    private static TextBlock Link(string text, string url)
    {
        var block = new TextBlock();
        var link = new Hyperlink(new Run(text));
        link.Click += (_, _) => BrowserLauncher.Open(url);
        block.Inlines.Add(link);
        return block;
    }

    private static FrameworkElement Centered(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }
}
