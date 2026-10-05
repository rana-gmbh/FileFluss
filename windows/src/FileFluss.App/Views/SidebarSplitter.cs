// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FileFluss.Core.Settings;

namespace FileFluss.App.Views;

/// <summary>
/// Makes a <see cref="GridSplitter"/> next to a sidebar behave like the macOS
/// <c>SidebarResizeHandle</c>: below the narrowest expanded width the sidebar snaps
/// magnetically to the icon-only detent or back, switching to icon-only live as it goes, and
/// a double-click restores the default width. <c>SidebarSplitter.Side="Left"</c> means the
/// sidebar is the column before the splitter, <c>"Right"</c> the column after it.
/// </summary>
public static class SidebarSplitter
{
    public static readonly DependencyProperty SideProperty = DependencyProperty.RegisterAttached(
        "Side", typeof(string), typeof(SidebarSplitter), new PropertyMetadata(null, OnSideChanged));

    /// <summary>Set by a double-click, consumed when the click's drag completes.</summary>
    private static readonly DependencyProperty ResetPendingProperty = DependencyProperty.RegisterAttached(
        "ResetPending", typeof(bool), typeof(SidebarSplitter), new PropertyMetadata(false));

    public static string? GetSide(DependencyObject element) => (string?)element.GetValue(SideProperty);

    public static void SetSide(DependencyObject element, string? value) => element.SetValue(SideProperty, value);

    private static bool IsLeft(GridSplitter splitter) => !string.Equals(GetSide(splitter), "Right", StringComparison.OrdinalIgnoreCase);

    private static void OnSideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not GridSplitter splitter || e.OldValue is not null)
        {
            return;
        }

        // Handled-too: the splitter's own class handler resizes first, then this snaps.
        splitter.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnDragDelta), handledEventsToo: true);
        splitter.MouseDoubleClick += OnDoubleClick;
    }

    /// <summary>The sidebar's column, or null when the splitter isn't in a grid.</summary>
    private static ColumnDefinition? SidebarColumn(GridSplitter splitter)
    {
        if (splitter.Parent is not Grid grid)
        {
            return null;
        }

        var index = Grid.GetColumn(splitter) + (IsLeft(splitter) ? -1 : 1);
        return index >= 0 && index < grid.ColumnDefinitions.Count ? grid.ColumnDefinitions[index] : null;
    }

    private static double CurrentWidth(ColumnDefinition column) => column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;

    private static void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var splitter = (GridSplitter)sender;
        if (SidebarColumn(splitter) is not { } column || CurrentWidth(column) <= 0)
        {
            return;
        }

        var width = (bool)splitter.GetValue(ResetPendingProperty) ? SidebarLayout.DefaultWidth : SidebarLayout.Snap(CurrentWidth(column));
        if (Math.Abs(width - CurrentWidth(column)) > 0.5)
        {
            column.Width = new GridLength(width);
        }

        SetCompact(splitter, SidebarLayout.IsCompact(width));
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var splitter = (GridSplitter)sender;
        if (e.ChangedButton != MouseButton.Left || SidebarColumn(splitter) is not { } column || CurrentWidth(column) <= 0)
        {
            return;
        }

        e.Handled = true;
        splitter.SetValue(ResetPendingProperty, true);
        column.Width = new GridLength(SidebarLayout.DefaultWidth);
        SetCompact(splitter, false);
    }

    /// <summary>Icon-only mode follows the width live, as on macOS where it is derived from it.</summary>
    private static void SetCompact(GridSplitter splitter, bool compact)
    {
        var settings = AppServices.Current.Settings;
        if (IsLeft(splitter))
        {
            settings.LeftSidebarCompact = compact;
        }
        else
        {
            settings.RightSidebarCompact = compact;
        }
    }

    /// <summary>
    /// The width the sidebar settles on when a drag (or the double-click's click) ends — snapped,
    /// or the default after a double-click; null when the sidebar is hidden.
    /// </summary>
    public static double? Settle(GridSplitter splitter)
    {
        var reset = (bool)splitter.GetValue(ResetPendingProperty);
        splitter.SetValue(ResetPendingProperty, false);
        if (SidebarColumn(splitter) is not { } column || CurrentWidth(column) <= 0)
        {
            return null;
        }

        var width = reset ? SidebarLayout.DefaultWidth : SidebarLayout.Snap(CurrentWidth(column));
        column.Width = new GridLength(width);
        return width;
    }

    public static bool IsLeftSidebar(GridSplitter splitter) => IsLeft(splitter);
}
