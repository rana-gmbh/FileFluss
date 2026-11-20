// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.Core.Compare;
using FileFluss.Core.Models;
using static FileFluss.App.Views.Sync.WindowKit;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Compare;

/// <summary>
/// One line of the comparison list. The list keeps these across updates (keyed by the
/// node's relative path), so selection and scroll position survive expanding a folder,
/// switching filters, toggling Compare Date and comparing again.
/// </summary>
internal sealed class CompareRowItem(CompareRow row)
{
    private CompareRow _row = row;

    public event EventHandler? Changed;

    public string Path => _row.Node.Path;

    public CompareRow Row
    {
        get => _row;
        set
        {
            if (!Equals(_row, value))
            {
                _row = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Re-renders an unchanged row (its texts after a language switch).</summary>
    public void Invalidate() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>What a screen reader announces for the row (the list item's name).</summary>
    public override string ToString()
    {
        var row = _row;
        var parts = new List<string> { row.Name };
        if (row.Status is { } status)
        {
            parts.Add(CompareColors.StatusName(status));
        }

        if (row.IsGroup)
        {
            parts.Add(row.IsExpanded ? L("Expanded") : L("Collapsed"));
            if (!string.IsNullOrEmpty(row.FolderSummary))
            {
                parts.Add(row.FolderSummary);
            }
        }
        else
        {
            if (row.NewerBadge is { } badge)
            {
                parts.Add(badge);
            }

            if (row.DetailText is { } detail)
            {
                parts.Add(detail);
            }
        }

        return string.Join(", ", parts);
    }
}

/// <summary>The status colours and names of the macOS comparison: ✓ green, ← blue, → orange, ⚠ red.</summary>
internal static class CompareColors
{
    private static readonly Dictionary<(FolderCompareStatus, bool), Brush> Cache = [];

    public static string Glyph(FolderCompareStatus status) => status switch
    {
        FolderCompareStatus.Identical => "", // CompletedSolid
        FolderCompareStatus.OnlyLeft => Glyphs.Back,
        FolderCompareStatus.OnlyRight => Glyphs.Forward,
        _ => Glyphs.Warning,
    };

    public static string StatusName(FolderCompareStatus status) => status switch
    {
        FolderCompareStatus.Identical => L("Identical"),
        FolderCompareStatus.OnlyLeft => L("Only Left"),
        FolderCompareStatus.OnlyRight => L("Only Right"),
        _ => L("Different"),
    };

    /// <summary>Readable on both themes; re-picked when Windows switches between light and dark.</summary>
    public static Brush For(FolderCompareStatus status)
    {
        var dark = ThemeBrushes.IsDark;
        if (Cache.TryGetValue((status, dark), out var cached))
        {
            return cached;
        }

        var color = status switch
        {
            FolderCompareStatus.Identical => dark ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x0F, 0x7B, 0x0F),
            FolderCompareStatus.OnlyLeft => dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0),
            FolderCompareStatus.OnlyRight => dark ? Color.FromRgb(0xFF, 0xA0, 0x4D) : Color.FromRgb(0xC2, 0x54, 0x00),
            _ => dark ? Color.FromRgb(0xFF, 0x99, 0xA4) : Color.FromRgb(0xC4, 0x2B, 0x1C),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Cache[(status, dark)] = brush;
        return brush;
    }

    /// <summary>The "Left newer" capsule: blue text on a faint blue fill.</summary>
    public static (Brush Fill, Brush Text) Badge()
    {
        var text = For(FolderCompareStatus.OnlyLeft);
        var color = ((SolidColorBrush)text).Color;
        var fill = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
        fill.Freeze();
        return (fill, text);
    }
}

/// <summary>
/// The visual of a <see cref="CompareRowItem"/>: disclosure chevron and folder summary for a
/// folder group; for a leaf the status icon, the file's icon and name, a "Left newer" badge
/// with Compare Date on, the size line and the two date lines. Built in code and refreshed
/// from the item directly — no bindings.
/// </summary>
internal sealed class CompareRowView : Grid
{
    private const double IndentStep = 16;

    private readonly TextBlock _chevron = Glyph(Glyphs.ChevronRight, 9);
    private readonly TextBlock _status = Glyph(string.Empty, 14);
    private readonly Image _icon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _badge = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _badgeText = new() { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _summary = Secondary(size: 12);
    private readonly TextBlock _detail = Secondary(size: 12);
    private readonly TextBlock _modified = Secondary(size: 12);
    private readonly TextBlock _created = Secondary(size: 12);
    private readonly ColumnDefinition _indent = new() { Width = new GridLength(0) };
    private CompareRowItem? _item;

    public CompareRowView()
    {
        Margin = new Thickness(0, 3, 6, 3);
        ColumnDefinitions.Add(_indent);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _chevron.HorizontalAlignment = HorizontalAlignment.Center;
        _chevron.VerticalAlignment = VerticalAlignment.Top;
        _chevron.Margin = new Thickness(0, 5, 0, 0);
        _chevron.Background = Brushes.Transparent; // hit-testable around the glyph
        _chevron.Cursor = Cursors.Hand;
        _chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _chevron.MouseLeftButtonDown += (_, e) =>
        {
            if (_item is { Row.IsGroup: true } item && Window.GetWindow(this) is CompareWindow window)
            {
                window.ToggleGroup(item);
                e.Handled = true;
            }
        };
        SetColumn(_chevron, 1);

        _status.VerticalAlignment = VerticalAlignment.Top;
        _status.Margin = new Thickness(0, 2, 0, 0);
        SetColumn(_status, 2);

        _badge.Child = _badgeText;
        var nameLine = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_icon, Dock.Left);
        DockPanel.SetDock(_badge, Dock.Right);
        nameLine.Children.Add(_icon);
        nameLine.Children.Add(_badge);
        nameLine.Children.Add(_name);
        _created.Opacity = 0.75;
        var text = new StackPanel { Children = { nameLine, _detail, _modified, _created } };
        SetColumn(text, 3);

        _summary.TextWrapping = TextWrapping.NoWrap;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(12, 0, 0, 0);
        SetColumn(_summary, 4);

        Children.Add(_chevron);
        Children.Add(_status);
        Children.Add(text);
        Children.Add(_summary);

        DataContextChanged += (_, _) => Attach(DataContext as CompareRowItem);
        Loaded += (_, _) => Attach(DataContext as CompareRowItem);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(CompareRowItem? item)
    {
        if (_item is not null)
        {
            _item.Changed -= OnItemChanged;
        }

        _item = item;
        if (item is not null)
        {
            item.Changed += OnItemChanged;
            Render(item.Row);
        }
    }

    private void OnItemChanged(object? sender, EventArgs e)
    {
        if (_item is { } item)
        {
            Render(item.Row);
        }
    }

    private void Render(CompareRow row)
    {
        _indent.Width = new GridLength(row.Depth * IndentStep);
        _chevron.Visibility = row.IsGroup ? Visibility.Visible : Visibility.Hidden;
        _chevron.Text = row.IsExpanded ? Glyphs.ChevronDown : Glyphs.ChevronRight;

        if (row.Status is { } status)
        {
            _status.Text = CompareColors.Glyph(status);
            _status.Foreground = CompareColors.For(status);
            _status.ToolTip = CompareColors.StatusName(status);
        }
        else
        {
            // A folder only known through its contents: the neutral folder glyph.
            _status.Text = Glyphs.Folder;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _status.ToolTip = null;
        }

        _icon.Source = row.IsDirectory
            ? IconCache.Folder()
            : IconCache.For(new CloudFileItem(row.Name, row.Name, row.Name, isDirectory: false, 0, CloudFileItem.UnknownDate), isLocal: false);
        _name.Text = row.Name;
        _name.ToolTip = row.Node.Path;

        if (row.NewerBadge is { } badge)
        {
            var (fill, text) = CompareColors.Badge();
            _badge.Background = fill;
            _badgeText.Foreground = text;
            _badgeText.Text = badge;
            _badge.Visibility = Visibility.Visible;
        }
        else
        {
            _badge.Visibility = Visibility.Collapsed;
        }

        Show(_summary, row.IsGroup ? row.FolderSummary : null);
        Show(_detail, row.DetailText);
        Show(_modified, row.ModifiedDetail);
        Show(_created, row.CreatedDetail);
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text ?? string.Empty;
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
