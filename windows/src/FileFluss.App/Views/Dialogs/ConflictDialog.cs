// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Dialogs;

/// <summary>
/// "An item named … already exists": both files side by side with dates, sizes and a
/// newer/older label, the four choices, and Apply to all — the macOS conflict sheet.
/// </summary>
public sealed class ConflictDialog : Window
{
    private readonly CheckBox _applyToAll;
    private ConflictChoice _choice = ConflictChoice.Stop;

    private ConflictDialog(ConflictInfo conflict)
    {
        Title = L("Replace or Skip");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel();
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xF2, 0xC2, 0x00)),
            Padding = new Thickness(20, 16, 20, 16),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = Glyphs.Warning, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 22, Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0x9B, 0x00)), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L("An item named \"{0}\" already exists in this location. What would you like to do?", conflict.Source.Name), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        root.Children.Add(banner);

        var cards = new Grid { Margin = new Thickness(20) };
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        var source = Card(L("Source"), conflict.Source, conflict.SourceProvider.IsLocal, Compare(conflict.Source, conflict.Destination));
        var destination = Card(L("Destination"), conflict.Destination, conflict.DestinationProvider.IsLocal, Compare(conflict.Destination, conflict.Source));
        Grid.SetColumn(destination, 2);
        cards.Children.Add(source);
        cards.Children.Add(destination);
        root.Children.Add(cards);

        _applyToAll = new CheckBox
        {
            Content = conflict.RemainingConflicts > 0 ? $"{L("Apply to all")} ({conflict.RemainingConflicts})" : L("Apply to all"),
            Margin = new Thickness(20, 0, 20, 12),
            IsEnabled = conflict.RemainingConflicts > 0,
        };
        root.Children.Add(_applyToAll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 4, 20, 20) };
        buttons.Children.Add(MakeButton(L("Stop"), ConflictChoice.Stop, isCancel: true));
        buttons.Children.Add(MakeButton(L("Skip"), ConflictChoice.Skip));
        buttons.Children.Add(MakeButton(L("Keep Both"), ConflictChoice.KeepBoth));
        buttons.Children.Add(MakeButton(L("Replace"), ConflictChoice.Replace, isDefault: true));
        root.Children.Add(buttons);
        Content = root;
    }

    public static ConflictResolution Ask(Window? owner, ConflictInfo conflict)
    {
        // A closed window can't own a dialog (WPF throws), and neither can the main window
        // while FileFluss closes: then the transfer stops instead.
        var fallback = Application.Current?.MainWindow;
        var parent = owner is { IsLoaded: true } ? owner : fallback is { IsLoaded: true } ? fallback : null;
        if (parent is null || parent.Dispatcher.HasShutdownStarted)
        {
            return new ConflictResolution(ConflictChoice.Stop, false);
        }

        var dialog = new ConflictDialog(conflict) { Owner = parent };
        dialog.ShowDialog();
        return new ConflictResolution(dialog._choice, dialog._applyToAll.IsChecked == true);
    }

    private Button MakeButton(string caption, ConflictChoice choice, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = caption,
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 5, 14, 6),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (isDefault && TryFindResource("AccentButtonStyle") is Style accent)
        {
            button.Style = accent;
        }

        button.Click += (_, _) =>
        {
            _choice = choice;
            DialogResult = true;
        };
        return button;
    }

    private static string Compare(CloudFileItem a, CloudFileItem b)
    {
        if (!a.HasKnownModificationDate || !b.HasKnownModificationDate)
        {
            return string.Empty;
        }

        var difference = a.ModificationDate - b.ModificationDate;
        if (Math.Abs(difference.TotalSeconds) < 2)
        {
            return L("Same date.");
        }

        return difference > TimeSpan.Zero ? L("This file is newer.") : L("This file is older.");
    }

    private static Border Card(string heading, CloudFileItem item, bool isLocal, string age)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = heading, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 8) });
        var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var icon = new Image { Source = IconCache.For(item, isLocal, 32), Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        nameRow.Children.Add(icon);
        nameRow.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(nameRow);
        if (age.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = age, Margin = new Thickness(0, 0, 0, 4) });
        }

        panel.Children.Add(new TextBlock { Text = item.HasKnownModificationDate ? item.ModificationDate.ToString("f", Culture) : "--", Opacity = 0.8 });
        panel.Children.Add(new TextBlock { Text = item.IsDirectory ? L("Folder") : ByteFormatter.Format(item.Size), Opacity = 0.8 });

        var card = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        card.SetResourceReference(Border.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
        return card;
    }
}
