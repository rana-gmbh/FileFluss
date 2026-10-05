// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Commands;
using static FileFluss.App.Views.Settings.SettingsKit;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Settings;

/// <summary>
/// Settings → Keyboard Map: pick a preset (Explorer or Commander style), then re-record any
/// command. Click a shortcut to record; Esc cancels, Backspace clears. A shortcut already in
/// use asks before it is moved.
/// </summary>
public sealed class KeyboardMapPage : StackPanel
{
    private readonly AppServices _services;
    private Button? _recording;
    private KeyboardCommand _recordingCommand;

    public KeyboardMapPage(AppServices services)
    {
        _services = services;
        Build();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Build()
    {
        Children.Clear();
        var s = _services.Settings;
        var presets = Enum.GetValues<KeyboardPreset>().Select(p => (p, L(p.DisplayName()))).ToList();
        Children.Add(Card(L("Preset"), L("Click a shortcut to record. Esc cancels."), Choice(presets, s.KeyboardPreset, value =>
        {
            s.KeyboardPreset = value;
            Build();
        })));

        foreach (var section in Enum.GetValues<KeyboardSection>())
        {
            Children.Add(Heading(L(section.DisplayName())));
            var list = new StackPanel();
            foreach (var command in Enum.GetValues<KeyboardCommand>().Where(c => c.Section() == section))
            {
                list.Children.Add(Row(command));
            }

            Children.Add(Wrap(list));
        }

        var resetAll = Button(L("Reset All to Preset Defaults"), () =>
        {
            _services.Keyboard.ResetAll();
            Build();
        });
        resetAll.HorizontalAlignment = HorizontalAlignment.Left;
        resetAll.Margin = new Thickness(0, 16, 0, 0);
        Children.Add(resetAll);
    }

    private FrameworkElement Row(KeyboardCommand command)
    {
        var binding = _services.Keyboard.BindingFor(command);
        var shortcut = new Button
        {
            Content = binding?.DisplayString is { Length: > 0 } text ? text : L("None"),
            MinWidth = 150,
            Padding = new Thickness(10, 3, 10, 4),
            Tag = command,
        };
        shortcut.Click += (_, _) => StartRecording(shortcut, command);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(shortcut);
        if (_services.Keyboard.IsCustomised(command))
        {
            var reset = new Button { Content = L("Reset"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 4) };
            reset.Click += (_, _) =>
            {
                _services.Keyboard.ResetToPresetDefault(command);
                Build();
            };
            buttons.Children.Add(reset);
        }

        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock { Text = L(command.DisplayName()), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void StartRecording(Button button, KeyboardCommand command)
    {
        if (_recording is not null)
        {
            Build();
        }

        _recording = button;
        _recordingCommand = command;
        button.Content = L("Press a shortcut…");
        button.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var command = _recordingCommand;
        _recording = null;
        if (key == Key.Escape)
        {
            Build();
            return;
        }

        if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            _services.Keyboard.SetBinding(command, null);
            Build();
            return;
        }

        if (MainWindow.ShortcutFromKey(e) is not { } pressed)
        {
            Build();
            return;
        }

        var conflicts = _services.Keyboard.Conflicts(pressed, command);
        if (conflicts.Count > 0)
        {
            var names = string.Join(", ", conflicts.Select(c => L(c.DisplayName())));
            if (!MessageDialog.Confirm(Window.GetWindow(this), L("Shortcut in use"),
                    L("{0} is already used by {1}. Use it for {2} instead?", pressed.DisplayString, names, L(command.DisplayName())), L("Reassign")))
            {
                Build();
                return;
            }

            foreach (var other in conflicts)
            {
                _services.Keyboard.SetBinding(other, null);
            }
        }

        _services.Keyboard.SetBinding(command, pressed);
        Build();
    }
}
