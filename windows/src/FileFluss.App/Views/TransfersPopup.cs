// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Models;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// The toolbar's Transfers drop-down: every transfer in both panels, running first, each with
/// its live line, Details and Cancel — reachable even when the sidebars are icon-only or
/// scrolled away (macOS 1.5).
/// </summary>
public static class TransfersPopup
{
    public static Popup Create(Window owner, TransferCenter transfers, ToggleButton anchor)
    {
        var list = new StackPanel();
        var empty = new StackPanel
        {
            Margin = new Thickness(8, 16, 8, 16),
            Children =
            {
                new TextBlock { Text = L("Nothing is being transferred."), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = L("Copies, moves, uploads and downloads show up here."), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) },
            },
        };
        var clear = new Button { Content = L("Clear Finished"), ToolTip = L("Remove transfers that have finished. Running transfers are left alone."), HorizontalAlignment = HorizontalAlignment.Right };
        clear.Click += (_, _) => transfers.ClearFinished();

        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children = { new TextBlock { Text = L("Transfers"), FontSize = 15, FontWeight = FontWeights.SemiBold } },
        });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(empty);
        body.Children.Add(clear);

        var card = new Border
        {
            Width = 420,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.25 },
            Margin = new Thickness(12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");

        var popup = new Popup
        {
            Child = card,
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        popup.Closed += (_, _) => anchor.IsChecked = false;

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var transfer in transfers.All)
            {
                list.Children.Add(Row(owner, transfers, transfer));
            }

            empty.Visibility = transfers.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = transfers.HasFinished ? Visibility.Visible : Visibility.Collapsed;
        }

        transfers.All.CollectionChanged += (_, _) => Rebuild();
        popup.Opened += (_, _) => Rebuild();
        return popup;
    }

    private static FrameworkElement Row(Window owner, TransferCenter transfers, TransferProgress transfer)
    {
        var status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
        var bar = new ProgressBar { Height = 6, Maximum = 1, Margin = new Thickness(0, 6, 0, 4) };
        var detail = new TextBlock { FontSize = 11.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
        var panel = new TextBlock { FontSize = 11, Opacity = 0.6, Text = transfer.Panel == PanelSide.Left ? L("Left panel") : L("Right panel") };

        var details = new Button { Content = L("Details"), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
        details.Click += (_, _) => TransferDetailsWindow.Show(owner, transfer);
        var action = new Button { Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
        action.Click += (_, _) =>
        {
            if (transfer.IsRunning)
            {
                if (MessageDialog.Confirm(owner, L("Cancel Transfer"), L("Files already transferred will remain. Any partial file currently in flight will be discarded."), L("Cancel Transfer"), destructive: true))
                {
                    transfer.Cancel();
                }
            }
            else
            {
                transfers.Remove(transfer);
            }
        };

        var header = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { details, action } };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(status);

        var row = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel { Children = { header, bar, detail, panel } },
        };
        row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

        void Update()
        {
            status.Text = transfer.StatusText;
            status.ToolTip = transfer.StatusText;
            bar.Value = transfer.Fraction;
            bar.Visibility = transfer.IsComplete ? Visibility.Collapsed : Visibility.Visible;
            detail.Text = transfer.LiveDetailLine ?? (transfer.IsComplete ? string.Empty : transfer.CurrentFileName);
            detail.Visibility = string.IsNullOrEmpty(detail.Text) ? Visibility.Collapsed : Visibility.Visible;
            action.Content = transfer.IsRunning ? L("Cancel") : L("Remove");
        }

        transfer.PropertyChanged += (_, _) => row.Dispatcher.BeginInvoke(Update);
        Update();
        return row;
    }
}
