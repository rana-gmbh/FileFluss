// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Dialogs;

/// <summary>Pick one of several options (e.g. which kDrive drive, which GoPro camera).</summary>
public sealed class ChoiceDialog : Window
{
    private readonly ListBox _list;

    private ChoiceDialog(string title, string message, IReadOnlyList<string> options)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox { ItemsSource = options, SelectedIndex = 0, MaxHeight = 320, Margin = new Thickness(0, 12, 0, 0) };
        _list.MouseDoubleClick += (_, _) => DialogResult = _list.SelectedIndex >= 0;

        var ok = new Button { Content = L("Choose"), IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent)
        {
            ok.Style = accent;
        }

        ok.Click += (_, _) => DialogResult = _list.SelectedIndex >= 0;
        var cancel = new Button { Content = L("Cancel"), IsCancel = true, MinWidth = 96 };
        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, ok } },
            },
        };
    }

    public static int? Ask(Window? owner, string title, string message, IReadOnlyList<string> options)
    {
        var dialog = new ChoiceDialog(title, message, options) { Owner = MessageDialog.OwnerOrMain(owner) };
        return dialog.ShowDialog() == true ? dialog._list.SelectedIndex : null;
    }
}
