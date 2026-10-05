// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileFluss.App.Services;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Dialogs;

public enum DialogIcon
{
    None,
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>A button of <see cref="MessageDialog"/>: its result value, caption, and role.</summary>
public sealed record DialogButton(string Result, string Caption, bool IsDefault = false, bool IsCancel = false, bool IsDestructive = false);

/// <summary>
/// A Windows 11 content dialog: title, message, optional text field or check box, and a row
/// of buttons with one accent default. Replaces <see cref="MessageBox"/>, which draws with
/// the old Win32 look and can't follow the app's language or theme.
/// </summary>
public sealed class MessageDialog : Window
{
    private readonly TextBox? _input;
    private readonly CheckBox? _check;

    private MessageDialog(
        string title,
        string message,
        DialogIcon icon,
        IReadOnlyList<DialogButton> buttons,
        string? inputText,
        bool selectStem,
        string? checkText,
        bool checkInitial)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var header = new DockPanel { LastChildFill = true };
        if (icon != DialogIcon.None)
        {
            var glyph = new TextBlock
            {
                FontFamily = new FontFamily(Glyphs.FontFamily),
                FontSize = 28,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Text = icon switch
                {
                    DialogIcon.Warning => Glyphs.Warning,
                    DialogIcon.Error => Glyphs.Error,
                    DialogIcon.Question => Glyphs.Help,
                    _ => Glyphs.Info,
                },
                Foreground = icon switch
                {
                    DialogIcon.Warning => new SolidColorBrush(Color.FromRgb(0xD8, 0x9B, 0x00)),
                    DialogIcon.Error => new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
                    _ => (Brush)FindResource(SystemColors.AccentColorBrushKey),
                },
            };
            DockPanel.SetDock(glyph, Dock.Left);
            header.Children.Add(glyph);
        }

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        if (!string.IsNullOrEmpty(message))
        {
            texts.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        }

        header.Children.Add(texts);
        body.Children.Add(header);

        if (inputText is not null)
        {
            _input = new TextBox { Text = inputText, Margin = new Thickness(0, 12, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(_input, string.IsNullOrEmpty(message) ? title : message);
            _input.Loaded += (_, _) =>
            {
                _input.Focus();
                var dot = inputText.LastIndexOf('.');
                if (selectStem && dot > 0)
                {
                    _input.Select(0, dot);
                }
                else
                {
                    _input.SelectAll();
                }
            };
            body.Children.Add(_input);
        }

        if (checkText is not null)
        {
            _check = new CheckBox { Content = checkText, IsChecked = checkInitial, Margin = new Thickness(0, 12, 0, 0) };
            body.Children.Add(_check);
        }

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var definition in buttons)
        {
            var button = new Button
            {
                Content = definition.Caption,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 5, 14, 6),
                IsDefault = definition.IsDefault,
                IsCancel = definition.IsCancel,
            };
            if (definition.IsDefault && TryFindResource("AccentButtonStyle") is Style accent)
            {
                button.Style = accent;
            }

            if (definition.IsDestructive)
            {
                button.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            }

            button.Click += (_, _) =>
            {
                Result = definition.Result;
                DialogResult = !definition.IsCancel;
            };
            buttonRow.Children.Add(button);
        }

        var footer = new Border
        {
            Child = buttonRow,
            Padding = new Thickness(24, 16, 24, 20),
        };
        footer.SetResourceReference(Border.BackgroundProperty, SystemColors.ControlBrushKey);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Result = buttons.FirstOrDefault(b => b.IsCancel)?.Result;
                DialogResult = false;
            }
        };
    }

    public string? Result { get; private set; }

    public string? InputText => _input?.Text;

    public bool IsChecked => _check?.IsChecked == true;

    public static string? Show(
        Window? owner,
        string title,
        string message,
        DialogIcon icon,
        params DialogButton[] buttons)
    {
        var dialog = new MessageDialog(title, message, icon, buttons, null, false, null, false) { Owner = OwnerOrMain(owner) };
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>OK / Cancel style confirmation. True when confirmed.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmCaption, bool destructive = false, DialogIcon icon = DialogIcon.Warning)
        => Show(owner, title, message, icon,
            new DialogButton("cancel", L("Cancel"), IsCancel: true),
            new DialogButton("ok", confirmCaption, IsDefault: !destructive, IsDestructive: destructive)) == "ok";

    /// <summary>Confirmation with a "don't ask again"-style check box. Null when cancelled.</summary>
    public static (bool Confirmed, bool Checked) ConfirmWithCheck(Window? owner, string title, string message, string confirmCaption, string checkText, bool destructive)
    {
        var dialog = new MessageDialog(title, message, DialogIcon.Warning,
            [new DialogButton("cancel", L("Cancel"), IsCancel: true), new DialogButton("ok", confirmCaption, IsDefault: !destructive, IsDestructive: destructive)],
            null, false, checkText, false) { Owner = OwnerOrMain(owner) };
        dialog.ShowDialog();
        return (dialog.Result == "ok", dialog.IsChecked);
    }

    /// <summary>A text prompt (new folder, rename). Null when cancelled or left empty.</summary>
    public static string? Prompt(Window? owner, string title, string message, string initial, string confirmCaption, bool selectStem = false)
    {
        var dialog = new MessageDialog(title, message, DialogIcon.None,
            [new DialogButton("cancel", L("Cancel"), IsCancel: true), new DialogButton("ok", confirmCaption, IsDefault: true)],
            initial, selectStem, null, false) { Owner = OwnerOrMain(owner) };
        return dialog.ShowDialog() == true && dialog.Result == "ok" && !string.IsNullOrWhiteSpace(dialog.InputText)
            ? dialog.InputText!.Trim()
            : null;
    }

    public static void Error(Window? owner, string title, string message)
        => Show(owner, title, message, DialogIcon.Error, new DialogButton("ok", L("OK"), IsDefault: true, IsCancel: true));

    public static void Info(Window? owner, string title, string message)
        => Show(owner, title, message, DialogIcon.Info, new DialogButton("ok", L("OK"), IsDefault: true, IsCancel: true));

    /// <summary>A window that can own a dialog: the one given, else the active or main window — never a closed one (WPF throws).</summary>
    internal static Window? OwnerOrMain(Window? owner)
    {
        var candidate = owner ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return candidate is { IsLoaded: true } ? candidate : null;
    }
}
