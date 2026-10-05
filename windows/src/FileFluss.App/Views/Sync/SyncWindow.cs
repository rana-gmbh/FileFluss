// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using Localization = FileFluss.Core.Localization;
using FileFluss.Core.Models;
using FileFluss.Core.Sync;
using static FileFluss.App.Views.Sync.WindowKit;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Sync;

/// <summary>
/// The Sync window (macOS <c>SyncPlannerView</c>): the two panels' folders, a direction, a
/// mode, Calculate to see the plan — what will be added, replaced and deleted, how much
/// travels, and the space left at the destination — then Start Sync, which runs in the
/// background in the destination panel's transfer list and closes the window.
///
/// <para>All logic lives in <see cref="SyncSession"/>. On macOS this is a sheet, so the
/// folders can't change while it is open; here the window is modeless and follows the
/// panels instead — browsing either panel starts a fresh session (keeping direction and mode)
/// whose plan has to be calculated again.</para>
/// </summary>
public sealed class SyncWindow : Window
{
    private static readonly Dictionary<Window, SyncWindow> Open = [];

    private readonly MainViewModel _vm;
    private readonly EndpointCard _leftCard = new();
    private readonly EndpointCard _rightCard = new();
    private readonly TextBlock _arrow = Glyph(Glyphs.Forward, 20);
    private readonly TextBlock _heading = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _directionLabel = SectionLabel();
    private readonly TextBlock _modeLabel = SectionLabel();
    private readonly TextBlock _planLabel = SectionLabel();
    private readonly RadioButton _leftToRight = new() { GroupName = "SyncDirection", MinWidth = 110, Padding = new Thickness(14, 5, 14, 6) };
    private readonly RadioButton _rightToLeft = new() { GroupName = "SyncDirection", MinWidth = 110, Padding = new Thickness(14, 5, 14, 6) };
    private readonly Dictionary<SyncMode, (RadioButton Button, TextBlock Title, TextBlock Subtitle)> _modes = [];
    private readonly CheckBox _confirm = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _confirmText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _calculate = new() { MinWidth = 96, Padding = new Thickness(12, 4, 12, 5) };
    private readonly ProgressBar _calculating = new() { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Grid _plan = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _hint = Secondary();
    private readonly TextBlock _spaceWarning = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _cancel = new() { MinWidth = 96, Padding = new Thickness(12, 5, 12, 6), IsCancel = true };
    private readonly Button _start = new() { MinWidth = 96, Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(8, 0, 0, 0) };

    private SyncSession _session;
    private PanelSnapshot _left;
    private PanelSnapshot _right;
    private bool _updating;
    private bool _closed;
    private int _refreshQueued;

    private SyncWindow(MainViewModel vm)
    {
        _vm = vm;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "SyncWindow");

        _left = PanelSnapshot.Of(vm.Left);
        _right = PanelSnapshot.Of(vm.Right);
        _session = NewSession(PlanDirection.LeftToRight, SyncMode.Newer);

        Content = Build();
        ApplyTexts();
        Refresh();

        vm.Left.LocationChanged += OnPanelMoved;
        vm.Right.LocationChanged += OnPanelMoved;
        Localization.LanguageChanged += OnLanguageChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            vm.Left.LocationChanged -= OnPanelMoved;
            vm.Right.LocationChanged -= OnPanelMoved;
            Localization.LanguageChanged -= OnLanguageChanged;
            _session.PropertyChanged -= OnSessionChanged;
            _session.CancelCalculation();
        };
    }

    /// <summary>Opens the Sync window for the two panels' folders, or brings the open one forward.</summary>
    public static void Show(Window owner, MainViewModel vm)
    {
        if (Open.TryGetValue(owner, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new SyncWindow(vm) { Owner = owner };
        Open[owner] = window;
        window.Closed += (_, _) => Open.Remove(owner);
        window.Show();
        window.Activate();
    }

    // MARK: - Layout

    private FrameworkElement Build()
    {
        // Endpoints: Left card, the direction arrow, Right card.
        var endpoints = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _arrow.HorizontalAlignment = HorizontalAlignment.Center;
        _arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(_arrow, 1);
        Grid.SetColumn(_rightCard, 2);
        endpoints.Children.Add(_leftCard);
        endpoints.Children.Add(_arrow);
        endpoints.Children.Add(_rightCard);

        // Direction: a two-segment toggle.
        var toggleStyle = TryFindResource(typeof(ToggleButton)) as Style;
        _leftToRight.Style = toggleStyle;
        _rightToLeft.Style = toggleStyle;
        _leftToRight.Margin = new Thickness(0, 0, 2, 0);
        AutomationProperties.SetAutomationId(_leftToRight, "SyncDirectionLeftToRight");
        AutomationProperties.SetAutomationId(_rightToLeft, "SyncDirectionRightToLeft");
        _leftToRight.Checked += (_, _) => SetDirection(PlanDirection.LeftToRight);
        _rightToLeft.Checked += (_, _) => SetDirection(PlanDirection.RightToLeft);
        var segments = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _leftToRight, _rightToLeft } };
        var direction = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(segments, Dock.Right);
        _directionLabel.VerticalAlignment = VerticalAlignment.Center;
        _directionLabel.Margin = new Thickness(0);
        direction.Children.Add(segments);
        direction.Children.Add(_directionLabel);

        // Mode: one radio per mode, title over a caption.
        var modes = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        modes.Children.Add(_modeLabel);
        foreach (var mode in SyncModeExtensions.All)
        {
            var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var subtitle = Secondary();
            var button = new RadioButton
            {
                GroupName = "SyncMode",
                Margin = new Thickness(0, 4, 0, 4),
                VerticalContentAlignment = VerticalAlignment.Top,
                Content = new StackPanel { Children = { title, subtitle } },
            };
            AutomationProperties.SetAutomationId(button, "SyncMode" + mode);
            var captured = mode;
            button.Checked += (_, _) =>
            {
                if (!_updating)
                {
                    _session.Mode = captured;
                }
            };
            _modes[mode] = (button, title, subtitle);
            modes.Children.Add(button);
        }

        _confirm.Content = _confirmText;
        _confirmText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        AutomationProperties.SetAutomationId(_confirm, "SyncConfirmDestructive");
        _confirm.Checked += (_, _) => SetConfirmed(true);
        _confirm.Unchecked += (_, _) => SetConfirmed(false);
        modes.Children.Add(_confirm);

        // Plan: heading with Calculate, then the figures or the hint.
        AutomationProperties.SetAutomationId(_calculate, "SyncCalculateButton");
        _calculate.Click += (_, _) => OnCalculate();
        var planHeader = new DockPanel();
        DockPanel.SetDock(_calculate, Dock.Right);
        _planLabel.VerticalAlignment = VerticalAlignment.Center;
        _planLabel.Margin = new Thickness(0);
        planHeader.Children.Add(_calculate);
        planHeader.Children.Add(_planLabel);
        _plan.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _plan.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hint.Margin = new Thickness(0, 6, 0, 0);
        _spaceWarning.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        AutomationProperties.SetAutomationId(_error, "SyncError");
        var plan = new StackPanel { Children = { planHeader, _calculating, _plan, _hint, _spaceWarning, _error } };

        // Footer.
        AutomationProperties.SetAutomationId(_cancel, "SyncCancelButton");
        AutomationProperties.SetAutomationId(_start, "SyncStartButton");
        Accent(_start);
        _cancel.Click += (_, _) => Close();
        _start.Click += (_, _) => OnStart();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _start } };

        return new StackPanel
        {
            Margin = new Thickness(24, 18, 24, 20),
            Children =
            {
                _heading,
                endpoints,
                direction,
                modes,
                plan,
                Divider(new Thickness(0, 16, 0, 14)),
                footer,
            },
        };
    }

    private static TextBlock SectionLabel() => new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    private void ApplyTexts()
    {
        Title = L("Sync");
        _heading.Text = L("Sync");
        _directionLabel.Text = L("Direction");
        _modeLabel.Text = L("Mode");
        _planLabel.Text = L("Plan");
        _leftToRight.Content = L("Left → Right");
        _rightToLeft.Content = L("Left ← Right");
        AutomationProperties.SetName(_leftToRight, L("Left → Right"));
        AutomationProperties.SetName(_rightToLeft, L("Left ← Right"));
        foreach (var (mode, (button, title, subtitle)) in _modes)
        {
            title.Text = mode.Title();
            subtitle.Text = mode.Subtitle();
            AutomationProperties.SetName(button, mode.Title());
            AutomationProperties.SetHelpText(button, mode.Subtitle());
        }

        _confirmText.Text = L("I understand files on the destination will be deleted.");
        AutomationProperties.SetName(_confirm, _confirmText.Text);
        _hint.Text = SyncSession.PlanHint;
        _spaceWarning.Text = "⚠ " + L("This sync would exceed the available space at the destination.");
        _cancel.Content = L("Cancel");
        _start.Content = L("Start Sync");
    }

    // MARK: - State

    private SyncSession NewSession(PlanDirection direction, SyncMode mode)
    {
        var session = new SyncSession(_left.Endpoint, _right.Endpoint) { Direction = direction, Mode = mode };
        session.PropertyChanged += OnSessionChanged;
        return session;
    }

    private void OnPanelMoved(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed)
        {
            return;
        }

        var direction = _session.Direction;
        var mode = _session.Mode;
        _session.PropertyChanged -= OnSessionChanged;
        _session.CancelCalculation();
        _left = PanelSnapshot.Of(_vm.Left);
        _right = PanelSnapshot.Of(_vm.Right);
        _session = NewSession(direction, mode);
        Refresh();
    });

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            Refresh();
        }
        else if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _refreshQueued = 0;
                Refresh();
            });
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ApplyTexts();
        Refresh();
    });

    private void SetDirection(PlanDirection direction)
    {
        if (!_updating)
        {
            _session.Direction = direction;
        }
    }

    private void SetConfirmed(bool confirmed)
    {
        if (!_updating)
        {
            _session.ConfirmDestructive = confirmed;
        }
    }

    /// <summary>Why the sync can't run whatever the plan says: an offline side, a read-only destination.</summary>
    private string? Blocker()
    {
        if (_left.Endpoint is { IsOffline: true } || _right.Endpoint is { IsOffline: true })
        {
            return L("It's not possible to sync with an offline folder.");
        }

        var destination = _session.DestinationSide == PanelSide.Left ? _left : _right;
        return destination.IsReadOnly ? L("This location is read-only.") : null;
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            var session = _session;
            _leftCard.Show(L("Left"), _left);
            _rightCard.Show(L("Right"), _right);

            var leftToRight = session.Direction == PlanDirection.LeftToRight;
            _arrow.Text = leftToRight ? Glyphs.Forward : Glyphs.Back;
            _leftToRight.IsChecked = leftToRight;
            _rightToLeft.IsChecked = !leftToRight;
            foreach (var (mode, (button, _, _)) in _modes)
            {
                button.IsChecked = mode == session.Mode;
            }

            _confirm.Visibility = session.RequiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
            _confirm.IsChecked = session.ConfirmDestructive;

            var calculating = session.IsCalculating;
            _calculate.Content = calculating ? L("Stop") : L("Calculate");
            AutomationProperties.SetName(_calculate, (string)_calculate.Content);
            _calculate.IsEnabled = calculating || session.CanCalculate;
            _calculating.Visibility = calculating ? Visibility.Visible : Visibility.Collapsed;

            var plan = session.Plan;
            FillPlan(session, plan);
            _hint.Visibility = plan is null ? Visibility.Visible : Visibility.Collapsed;
            _spaceWarning.Visibility = plan is not null && session.WouldExceedAvailableSpace ? Visibility.Visible : Visibility.Collapsed;

            var message = session.ErrorMessage ?? Blocker();
            _error.Text = message ?? string.Empty;
            _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;

            var canStart = session.CanStart && Blocker() is null;
            _start.IsEnabled = canStart;

            // Enter starts once there is something to start, and calculates before that.
            _start.IsDefault = canStart;
            _calculate.IsDefault = !canStart && _calculate.IsEnabled && !calculating;
        }
        finally
        {
            _updating = false;
        }
    }

    private void FillPlan(SyncSession session, SyncPlan? plan)
    {
        _plan.Children.Clear();
        _plan.RowDefinitions.Clear();
        _plan.Visibility = plan is null ? Visibility.Collapsed : Visibility.Visible;
        if (plan is null)
        {
            return;
        }

        AddRow(L("Files to add:"), plan.FilesToAdd.ToString("N0", Culture));
        AddRow(L("Files to replace:"), plan.FilesToReplace.ToString("N0", Culture));
        AddRow(L("Files to delete:"), plan.FilesToDelete.ToString("N0", Culture), danger: plan.FilesToDelete > 0);
        AddRow(L("Folders to add:"), plan.FoldersToAdd.ToString("N0", Culture));
        AddRow(L("Folders to delete:"), plan.FoldersToDelete.ToString("N0", Culture), danger: plan.FoldersToDelete > 0);
        AddDivider();
        if (session.DownloadText is { } download)
        {
            AddRow(L("Download:"), download);
        }

        if (session.UploadText is { } upload)
        {
            AddRow(L("Upload:"), upload);
        }

        if (session.TransferText is { } transfer)
        {
            AddRow(L("Transfer:"), transfer);
        }

        if (session.AvailableText is { } available)
        {
            AddDivider();
            AddRow(L("Available:"), available);
            AddRow(L("After sync:"), session.AfterSyncText ?? string.Empty, danger: session.WouldExceedAvailableSpace);
        }

        void AddRow(string label, string value, bool danger = false)
        {
            var row = _plan.RowDefinitions.Count;
            _plan.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Secondary(label, 13);
            name.Margin = new Thickness(0, 2, 12, 2);
            var figure = new TextBlock { Text = value, Margin = new Thickness(0, 2, 0, 2) };
            if (danger)
            {
                figure.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            }

            AutomationProperties.SetName(figure, $"{label} {value}");
            Grid.SetRow(name, row);
            Grid.SetRow(figure, row);
            Grid.SetColumn(figure, 1);
            _plan.Children.Add(name);
            _plan.Children.Add(figure);
        }

        void AddDivider()
        {
            var row = _plan.RowDefinitions.Count;
            _plan.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = Divider(new Thickness(0, 5, 0, 5));
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, 2);
            _plan.Children.Add(line);
        }
    }

    // MARK: - Actions

    private void OnCalculate()
    {
        if (_session.IsCalculating)
        {
            _session.CancelCalculation();
            return;
        }

        _ = _session.CalculateAsync();
    }

    private void OnStart()
    {
        if (Blocker() is not null || !_session.CanStart)
        {
            return;
        }

        var destination = _session.DestinationSide == PanelSide.Left ? _left : _right;
        var transfers = _vm.Transfers;
        var vm = _vm;
        var progress = _session.StartSync(p => transfers.Track(p, _ => AfterSync(vm, destination)));
        if (progress is not null)
        {
            Close();
        }
    }

    /// <summary>Shows the result in the destination panel (and the other one, if it shows the same folder).</summary>
    private static void AfterSync(MainViewModel vm, PanelSnapshot destination)
    {
        foreach (var panel in new[] { vm.Left, vm.Right })
        {
            if (panel.Location == destination.Location || panel.Side == destination.Side && panel.Location.AccountId == destination.Location.AccountId)
            {
                _ = panel.RefreshAsync(quiet: true);
            }
        }

        if (destination.Location.AccountId is { } id)
        {
            vm.Services.Accounts.InvalidateQuota(id);
        }
    }
}
