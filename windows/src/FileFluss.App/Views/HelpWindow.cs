// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FileFluss.App.Services;
using static FileFluss.Core.Localization;

using Glyphs = FileFluss.App.Services.Glyphs;

namespace FileFluss.App.Views;

/// <summary>
/// FileFluss Help (F1): the macOS help book, section for section. The text is looked up by the
/// macOS keys, so every translation carries over; Windows-specific wording comes from the
/// Windows catalogue's overrides of the same keys.
/// </summary>
public sealed partial class HelpWindow : Window
{
    private static HelpWindow? _open;

    private enum Kind
    {
        Paragraph,
        Heading,
        Bullet,
        Tip,
    }

    private sealed record Block(Kind Kind, string Key);

    private sealed record Section(string Title, string Glyph, Block[] Blocks);

    private static Block P(string key) => new(Kind.Paragraph, key);

    private static Block H(string key) => new(Kind.Heading, key);

    private static Block B(string key) => new(Kind.Bullet, key);

    private static Block T(string key) => new(Kind.Tip, key);

    private static readonly Section[] Sections =
    [
        new("Overview", Glyphs.Info,
        [
            P("FileFluss is a dual‑panel file manager for macOS that lets you work with local folders and multiple cloud storage accounts side by side. It is built around a left and right panel — copy, move, sync, and compare files between them with a few clicks."),
            H("At a glance"),
            B("**Two independent panels**, each can show a local folder or a connected cloud account."),
            B("**Drag & drop** between panels to copy or move files."),
            B("**Sync** to make the two sides match (mirror, newer, or additive)."),
            B("**Compare** to see which files differ, are missing, or match between the two sides."),
            B("**Search** across local files and every connected cloud account at once."),
            H("Toolbar"),
            B("**Search** (⌘F) — open the search window."),
            B("**Sync** — open the Sync planner."),
            B("**Compare** — open the Compare Folders window."),
            B("**Refresh** — reload both panels."),
            B("**Hidden Files** — toggle showing dotfiles in the active panel."),
            B("**Copy Mode** / **Move Mode** — force every drag & drop to that operation."),
        ]),
        new("Two‑Panel Layout", Glyphs.TwoPanes,
        [
            P("FileFluss shows a left panel and a right panel. The active panel is marked with a thin accent line at the top — most actions (toolbar buttons, keyboard shortcuts, the search target) apply to whichever panel is active."),
            H("Switching panels"),
            B("Click anywhere inside a panel to make it active."),
            B("The sidebar of each panel is independent — choose a different favourite, location, or cloud account on each side."),
            H("The path bar"),
            B("The breadcrumb at the top of each panel shows the current folder path. Click any segment to navigate up."),
            B("You can drop files onto a path‑bar segment to move/copy them into that folder."),
            H("Cloud accounts on both sides"),
            P("If you open the same cloud account in both panels (e.g. kDrive on the left and on the right), the two panels behave **independently**: you can browse to different folders on each side and drag between them."),
            T("Dragging a file from the same cloud account on the left to a folder on the right does an **in‑cloud** move/copy — the data stays on the server, no re‑upload."),
        ]),
        new("Adding Cloud Accounts", Glyphs.Cloud,
        [
            P("Cloud accounts are connected once and then appear in the sidebar of both panels."),
            H("Add an account"),
            B("Open **Settings → Cloud Accounts** (⌘,)."),
            B("Click **Add Cloud Account** and pick the provider (kDrive, pCloud, OneDrive, Google Drive, Dropbox, MEGA, Koofr, NextCloud, WebDAV, SFTP, GMX Cloud, WordPress, …)."),
            B("Sign in via the provider's authentication flow. FileFluss stores the tokens securely in your macOS Keychain."),
            B("The new account immediately appears in both panels' sidebars."),
            H("Use an account"),
            B("Click the account in either sidebar to load it in that panel."),
            B("Navigate folders the same way as local — double‑click to enter, or click breadcrumbs to go back up."),
            H("Remove an account"),
            B("In **Settings → Cloud Accounts**, click the trash icon next to the account."),
            B("FileFluss disconnects the account, removes the cached login, and clears its panels."),
            T("Some providers expose a sandboxed root (e.g. WordPress' media library). Behaviour like *create folder* may be unavailable for those providers — FileFluss greys out the actions that don't apply."),
        ]),
        new("Copying and Moving Files", Glyphs.Copy,
        [
            P("There are several ways to move or copy files between the two panels:"),
            H("Drag & drop"),
            B("Drag selected files from one panel and drop them on the other."),
            B("By default a prompt appears asking *Move or Copy?*."),
            B("Drop directly onto a folder row to land inside that folder, or onto the table background to drop into the panel's current directory."),
            B("You can also drop onto a breadcrumb in the path bar to land in that ancestor folder."),
            H("Right‑click"),
            B("Select files, right‑click, and choose **Copy to Other Panel** or **Move to Other Panel**."),
            B("Same effect as drag & drop, but no prompt — the action is taken immediately."),
            H("Conflicts"),
            P("If a file with the same name already exists at the destination, FileFluss asks how to resolve the conflict (replace, keep both, skip)."),
            T("If you find the *Move or Copy?* prompt repetitive, enable **Copy Mode** or **Move Mode** in the toolbar — see the next section."),
        ]),
        new("Copy Mode and Move Mode", Glyphs.MoveTo,
        [
            P("**Copy Mode** and **Move Mode** are two toolbar toggles that change how every drag & drop is interpreted."),
            H("Copy Mode"),
            B("When active, every drop is automatically a **copy** — the *Move or Copy?* prompt is skipped."),
            B("Useful when you're duplicating a lot of files between folders or panels."),
            H("Move Mode"),
            B("When active, every drop is automatically a **move** — files are removed from the source."),
            B("Useful when re‑organising a folder."),
            H("Important"),
            B("The two toggles are **mutually exclusive** — turning one on turns the other off."),
            B("Both reset to off when FileFluss relaunches, so a stuck *Move Mode* can't silently destroy files across sessions."),
            T("When neither mode is on, FileFluss falls back to the standard *Move or Copy?* prompt."),
        ]),
        new("Sync", Glyphs.Sync,
        [
            P("**Sync** makes the destination folder match the source folder according to the rule you choose. Use it for one‑shot replications between local and cloud, or between two cloud accounts."),
            H("Run a sync"),
            B("Open one folder on the left and one on the right."),
            B("Click **Sync** in the toolbar."),
            B("Choose the **direction** (Left → Right or Right → Left)."),
            B("Choose the **mode** (see below)."),
            B("FileFluss enumerates both sides and shows a plan: how many files will be added, replaced, deleted, and how many bytes will be transferred. Click **Start Sync**."),
            H("Modes"),
            B("**Newer** — copy files from the source whose modification date is newer than the destination's. Existing files only get replaced if they're truly newer."),
            B("**Mirror** — make the destination an exact copy of the source. **Files at the destination that are not on the source are deleted.** This is destructive — FileFluss requires explicit confirmation."),
            B("**Additive** — only copy files that are missing on the destination. If a file already exists, the source is added next to it with a unique name. Nothing is overwritten or deleted."),
            T("Sync runs in the background — you can keep working in either panel while it transfers. Progress shows in the destination panel's sidebar; click it to see details."),
        ]),
        new("Compare Folders", Glyphs.Compare,
        [
            P("**Compare** shows you the differences between the two panels' folders without changing anything."),
            H("Run a comparison"),
            B("Open one folder on the left and one on the right."),
            B("Click **Compare** in the toolbar."),
            B("FileFluss enumerates both sides recursively and opens a window with the result."),
            H("Reading the result"),
            B("**Different** (red ⚠) — file exists on both sides but its size (or modification date, if enabled) differs."),
            B("**Only on Left** (← blue) — file exists only in the left folder."),
            B("**Only on Right** (→ orange) — file exists only in the right folder."),
            B("**Identical** (✓ green) — file matches on both sides."),
            B("Use the **filter chips** at the top to narrow to one status, e.g. show only differences."),
            H("Compare Date"),
            B("Off by default — only file size is compared."),
            B("When on, files with the same size but a different modification date count as **Different**, and each row shows both modify and create timestamps along with a *Left newer* / *Right newer* badge."),
            H("Compare Again"),
            B("The comparison only re‑runs when you click **Compare** in the toolbar or **Compare Again** in the window — browsing folders while the window is open does not trigger a fresh scan."),
        ]),
        new("Search", Glyphs.Search,
        [
            P("**Search** queries the active local panel and every connected cloud account simultaneously."),
            H("Run a search"),
            B("Click **Search** in the toolbar, or press **⌘F**."),
            B("Type a query — results stream in as each source responds."),
            B("Filter by source using the chips below the search field."),
            H("Acting on results"),
            B("**Double‑click** a result to load its parent folder in the active panel."),
            B("**Right‑click** to choose **Open in Left Panel** or **Open in Right Panel** explicitly."),
            B("The search window stays open after picking a result, so you can pick more."),
            T("The search window is movable and resizable — its size is remembered across launches."),
        ]),
        new("Opening Files", Glyphs.OpenInNew,
        [
            P("Files on local panels and on cloud accounts open the same way."),
            H("Open in the default app"),
            B("**Double‑click** any file. Word documents open in Word, PDFs in Preview, images in your image viewer, and so on."),
            B("Cloud files are downloaded to a temporary cache the first time, so reopening the same file is instant."),
            B("Edits made in the local app are **not** synced back to the cloud automatically — use Sync or drag the modified file back if you want it uploaded."),
            H("Quick Look"),
            B("Select a file and press **Space** to preview it without opening it."),
        ]),
    ];

    private readonly ListBox _nav = new() { Width = 230, BorderThickness = new Thickness(0), Margin = new Thickness(8, 12, 8, 12) };
    private readonly StackPanel _body = new() { Margin = new Thickness(32, 20, 32, 28), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

    private HelpWindow()
    {
        Title = L("FileFluss Help");
        Width = 1000;
        Height = 700;
        MinWidth = 700;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _nav.Background = Brushes.Transparent;
        foreach (var section in Sections)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
            row.Children.Add(new TextBlock { Text = section.Glyph, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = L(section.Title), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = section });
        }

        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: Section section })
            {
                Render(section);
            }
        };
        var root = new DockPanel();
        DockPanel.SetDock(_nav, Dock.Left);
        root.Children.Add(_nav);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _nav.SelectedIndex = 0;
        Closed += (_, _) => _open = null;
    }

    public static void Show(Window owner)
    {
        _open ??= new HelpWindow { Owner = owner };
        _open.Show();
        _open.Activate();
    }

    private void Render(Section section)
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = L(section.Title), FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var block in section.Blocks)
        {
            var text = Markdown(L(block.Key));
            switch (block.Kind)
            {
                case Kind.Heading:
                    text.FontSize = 17;
                    text.FontWeight = FontWeights.SemiBold;
                    text.Margin = new Thickness(0, 14, 0, 4);
                    _body.Children.Add(text);
                    break;
                case Kind.Bullet:
                    var bullet = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    var dot = new TextBlock { Text = "•", Margin = new Thickness(4, 0, 10, 0), Opacity = 0.6 };
                    DockPanel.SetDock(dot, Dock.Left);
                    bullet.Children.Add(dot);
                    bullet.Children.Add(text);
                    _body.Children.Add(bullet);
                    break;
                case Kind.Tip:
                    var tip = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF2, 0xC2, 0x00)),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin = new Thickness(0, 12, 0, 4),
                        Child = new DockPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "", FontFamily = new FontFamily(Glyphs.FontFamily), Margin = new Thickness(0, 2, 10, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0x9B, 0x00)) },
                                text,
                            },
                        },
                    };
                    _body.Children.Add(tip);
                    break;
                default:
                    text.Margin = new Thickness(0, 2, 0, 6);
                    _body.Children.Add(text);
                    break;
            }
        }
    }

    /// <summary>Renders the help strings' **bold** and *italic* markup.</summary>
    private static TextBlock Markdown(string source)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        foreach (Match match in Emphasis().Matches(source))
        {
            if (match.Groups["bold"].Success)
            {
                block.Inlines.Add(new Bold(new Run(match.Groups["bold"].Value)));
            }
            else if (match.Groups["italic"].Success)
            {
                block.Inlines.Add(new Italic(new Run(match.Groups["italic"].Value)));
            }
            else
            {
                block.Inlines.Add(new Run(match.Value));
            }
        }

        return block;
    }

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|\*(?<italic>[^*]+?)\*|[^*]+|\*")]
    private static partial Regex Emphasis();
}
