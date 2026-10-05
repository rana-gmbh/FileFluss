// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Formatting;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// Transfer Details: the file moving right now with its own progress bar and phase, live
/// speed, how much of how much, time left, and every item finished so far — figures shown
/// only when they are genuinely measured.
/// </summary>
public sealed class TransferDetailsWindow : Window
{
    private static readonly Dictionary<Guid, TransferDetailsWindow> Open = [];
    private readonly TransferProgress _transfer;
    private readonly TextBlock _headline = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _phase = new() { FontSize = 11, Padding = new Thickness(6, 1, 6, 2) };
    private readonly TextBlock _currentName = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 4) };
    private readonly ProgressBar _fileBar = new() { Height = 5, Maximum = 1 };
    private readonly TextBlock _fileBytes = new();
    private readonly ProgressBar _totalBar = new() { Height = 6, Maximum = 1, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _currentPanel = new();
    private readonly Grid _figures = new();
    private readonly ListBox _items = new() { MaxHeight = 220, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _itemsHeader;
    private readonly Button _cancel;

    private TransferDetailsWindow(TransferProgress transfer)
    {
        _transfer = transfer;
        Title = L("Transfer Details");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var badge = new Border { CornerRadius = new CornerRadius(4), Child = _phase, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        _currentPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = L("Current file"), Opacity = 0.7 }, badge },
        });
        _currentPanel.Children.Add(_currentName);
        _currentPanel.Children.Add(_fileBar);
        _fileBytes.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _fileBytes.FontSize = 11.5;
        _currentPanel.Children.Add(_fileBytes);

        _figures.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _figures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _itemsHeader = new TextBlock { Opacity = 0.7, Margin = new Thickness(0, 12, 0, 4) };

        _cancel = new Button { Content = L("Cancel Transfer"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _cancel.Click += (_, _) =>
        {
            if (MessageDialog.Confirm(this, L("Cancel Transfer"), L("Files already transferred will remain. Any partial file currently in flight will be discarded."), L("Cancel Transfer"), destructive: true))
            {
                _transfer.Cancel();
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(22, 18, 22, 20),
            Children =
            {
                _headline,
                _totalBar,
                Divider(),
                _currentPanel,
                _figures,
                _itemsHeader,
                _items,
                _cancel,
            },
        };

        transfer.PropertyChanged += OnTransferChanged;
        Closed += (_, _) =>
        {
            transfer.PropertyChanged -= OnTransferChanged;
            Open.Remove(transfer.Id);
        };
        Refresh();
    }

    public static void Show(Window? owner, TransferProgress transfer)
    {
        if (Open.TryGetValue(transfer.Id, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new TransferDetailsWindow(transfer) { Owner = owner };
        Open[transfer.Id] = window;
        window.Show();
    }

    private static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 10) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    private void OnTransferChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var t = _transfer;
        _headline.Text = t.StatusText;
        _totalBar.Value = t.Fraction;
        _totalBar.Visibility = t.IsComplete ? Visibility.Collapsed : Visibility.Visible;

        var showCurrent = !t.IsComplete && t.CurrentFileName.Length > 0;
        _currentPanel.Visibility = showCurrent ? Visibility.Visible : Visibility.Collapsed;
        if (showCurrent)
        {
            _phase.Text = t.Phase switch
            {
                TransferPhase.Uploading => L("Uploading"),
                TransferPhase.Downloading => L("Downloading"),
                TransferPhase.Preparing => L("Preparing…"),
                _ => TransferProgress.OperationLabel(t.Operation),
            };
            _currentName.Text = t.CurrentFileName;
            var fraction = t.CurrentFileFraction;
            _fileBar.Visibility = fraction is null ? Visibility.Collapsed : Visibility.Visible;
            _fileBar.Value = fraction ?? 0;
            _fileBytes.Text = t.CurrentFileSize > 0 && t.CurrentFileBytes > 0
                ? L("{0} of {1}", ByteFormatter.Format(Math.Min(t.CurrentFileBytes, t.CurrentFileSize)), ByteFormatter.Format(t.CurrentFileSize))
                : string.Empty;
        }

        _figures.Children.Clear();
        _figures.RowDefinitions.Clear();
        var rows = new List<(string, string)>();
        if (!t.IsComplete)
        {
            if (t.LiveSpeedText is { } speed)
            {
                rows.Add((L("Speed"), speed));
            }

            if (t.MovedOfExpectedText is { } moved)
            {
                rows.Add((L("Transferred"), moved));
            }

            if (t.TimeRemaining is { } eta)
            {
                rows.Add((L("Time Left"), ByteFormatter.FormatDuration(eta)));
            }
        }
        else
        {
            if (t.EndTime is { } end)
            {
                rows.Add((L("Finished"), end.ToString("T", Culture)));
            }

            if (t.MovedBytes > 0)
            {
                rows.Add((L("Total Size"), ByteFormatter.Format(t.IsCloudToCloud ? t.MovedBytes / 2 : t.MovedBytes)));
                rows.Add((L("Avg. Speed"), t.AverageSpeedText));
            }
        }

        rows.Add((L("Operation"), TransferProgress.OperationLabel(t.Operation)));
        rows.Add((L("Elapsed"), ByteFormatter.FormatDuration(t.Duration)));
        for (var i = 0; i < rows.Count; i++)
        {
            _figures.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Item1, Opacity = 0.7, Margin = new Thickness(0, 3, 14, 3) };
            var value = new TextBlock { Text = rows[i].Item2, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            _figures.Children.Add(label);
            _figures.Children.Add(value);
        }

        var results = t.Results;
        _itemsHeader.Text = t.IsComplete ? L("Items ({0})", results.Count) : L("Finished so far ({0})", results.Count);
        _itemsHeader.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Items.Count != results.Count)
        {
            _items.ItemsSource = results.Select(r => new TextBlock
            {
                Text = r.Status switch
                {
                    TransferItemStatus.Failed => $"✕  {r.Name} — {r.ErrorMessage}",
                    TransferItemStatus.Skipped => $"↷  {r.Name}",
                    _ => $"✓  {r.Name}",
                },
                TextWrapping = TextWrapping.Wrap,
                Foreground = r.Status == TransferItemStatus.Failed ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("TextFillColorPrimaryBrush"),
            }).ToList();
        }

        _cancel.Visibility = t.IsComplete ? Visibility.Collapsed : Visibility.Visible;
    }
}
