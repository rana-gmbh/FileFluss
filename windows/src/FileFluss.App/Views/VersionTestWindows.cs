// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

// Debug builds only, with the rest of the version test (see Services/VersionTestRunner.cs).
#if DEBUG

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;

namespace FileFluss.App.Views;

/// <summary>
/// The macOS run's floating progress panel: account, bar, current step, Cancel. Non-modal,
/// so the app stays usable; a full pass is minutes of work and without it the app looks hung.
/// Progress calls arrive from the worker thread and are marshalled here.
/// </summary>
public sealed class VersionTestProgressWindow : Window, IVersionTestProgress
{
    private readonly TextBlock _account;
    private readonly TextBlock _step;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private readonly int _total;
    private int _completed;
    private volatile bool _cancelled;
    private bool _finished;

    public VersionTestProgressWindow(int total)
    {
        _total = Math.Max(1, total);
        Title = "Version Test";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _account = new TextBlock { Text = "Starting…", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _bar = new ProgressBar { Minimum = 0, Maximum = _total, Height = 6, Margin = new Thickness(0, 10, 0, 8) };
        _step = new TextBlock { FontSize = 11, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
        _cancel = new Button { Content = "Cancel", MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        _cancel.Click += (_, _) => Cancel();

        Content = new StackPanel { Margin = new Thickness(16), Children = { _account, _bar, _step, _cancel } };
    }

    public bool IsCancelled => _cancelled;

    public void BeginAccount(string name, int index, int count) => Post(() => _account.Text = $"{name}  ({index} of {count})");

    public void BeginStep(string label)
        => Post(() =>
        {
            // Once cancelling, keep the explanation in place rather than the next step's name.
            if (!_cancelled)
            {
                _step.Text = label;
            }
        });

    public void FinishStep()
        => Post(() =>
        {
            // The step count is an estimate (share-link phases vary), so never run past the end.
            _completed++;
            _bar.Value = Math.Min(_completed, _total);
        });

    /// <summary>Closes the window for good once the run has ended.</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }

    /// <summary>The close box cancels instead: the run still has to clean up its test folder.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            Cancel();
        }

        base.OnClosing(e);
    }

    private void Cancel()
    {
        _cancelled = true;
        _cancel.IsEnabled = false;
        _step.Text = "Cancelling — finishing the current step and cleaning up…";
    }

    private void Post(Action action) => _ = Dispatcher.BeginInvoke(action);
}

/// <summary>The macOS "Run Version Test" alert: one check box per account, Select All / None.</summary>
public sealed class VersionTestAccountDialog : Window
{
    private readonly List<(CheckBox Box, CloudAccount Account)> _boxes = [];

    private VersionTestAccountDialog(IReadOnlyList<CloudAccount> accounts, IReadOnlySet<Guid> remembered)
    {
        Title = "Run Version Test";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new StackPanel();
        foreach (var account in accounts)
        {
            var box = new CheckBox
            {
                Content = $"{account.DisplayName} — {account.ProviderType.DisplayName()}",
                // First run has nothing remembered: test everything.
                IsChecked = remembered.Count == 0 || remembered.Contains(account.Id),
                Margin = new Thickness(0, 2, 0, 2),
            };
            list.Children.Add(box);
            _boxes.Add((box, account));
        }

        var toggleAll = new Button { Content = "Select All / None", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 3, 10, 4) };
        toggleAll.Click += (_, _) =>
        {
            var turningOn = _boxes.Any(b => b.Box.IsChecked != true);
            foreach (var (box, _) in _boxes)
            {
                box.IsChecked = turningOn;
            }
        };

        var run = new Button { Content = "Run Test", IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent)
        {
            run.Style = accent;
        }

        run.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children =
            {
                new TextBlock { Text = "Run Version Test", FontSize = 18, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "Choose the accounts to test. Each one runs the full create/upload/replace/share/delete pass.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 12),
                },
                new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                toggleAll,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, run } },
            },
        };
    }

    /// <summary>The checked accounts, or null when cancelled.</summary>
    public static List<CloudAccount>? Ask(Window owner, IReadOnlyList<CloudAccount> accounts, IReadOnlySet<Guid> remembered)
    {
        var dialog = new VersionTestAccountDialog(accounts, remembered) { Owner = owner };
        return dialog.ShowDialog() == true
            ? dialog._boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Account).ToList()
            : null;
    }
}

#endif
