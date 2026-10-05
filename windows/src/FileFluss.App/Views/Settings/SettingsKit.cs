// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileFluss.App.Views.Settings;

/// <summary>Builders for Windows 11 Settings-style cards: a title, an optional description, a control on the right.</summary>
internal static class SettingsKit
{
    public static Border Card(string title, string? description, FrameworkElement? control, FrameworkElement? below = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description))
        {
            var caption = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            text.Children.Add(caption);
        }

        var row = new DockPanel();
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(control, Dock.Right);
            row.Children.Add(control);
        }

        row.Children.Add(text);
        var content = new StackPanel { Children = { row } };
        if (below is not null)
        {
            below.Margin = new Thickness(0, 10, 0, 0);
            content.Children.Add(below);
        }

        return Wrap(content);
    }

    public static Border Wrap(FrameworkElement content)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 4),
            Child = content,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 18, 0, 8),
    };

    /// <summary>A Windows 11 toggle switch bound to a getter/setter.</summary>
    public static FrameworkElement Toggle(Func<bool> get, Action<bool> set)
    {
        var toggle = new CheckBox { IsChecked = get() };
        if (Application.Current.TryFindResource("ToggleSwitchStyle") is Style style)
        {
            toggle.Style = style;
        }

        toggle.Checked += (_, _) => set(true);
        toggle.Unchecked += (_, _) => set(false);
        return toggle;
    }

    public static ComboBox Choice<T>(IReadOnlyList<(T Value, string Label)> options, T current, Action<T> set, double width = 220)
    {
        var combo = new ComboBox { MinWidth = width, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        combo.ItemsSource = options.Select(o => new ChoiceItem(o.Value!, o.Label)).ToList();
        combo.SelectedValue = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedValue is T value)
            {
                set(value);
            }
        };
        return combo;
    }

    public sealed record ChoiceItem(object Value, string Label);

    public static Button Button(string caption, Action click, bool accent = false)
    {
        var button = new Button { Content = caption, MinWidth = 96, Padding = new Thickness(12, 5, 12, 6) };
        if (accent && Application.Current.TryFindResource("AccentButtonStyle") is Style style)
        {
            button.Style = style;
        }

        button.Click += (_, _) => click();
        return button;
    }

    public static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 4, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    public static TextBlock Glyph(string glyph, double size = 16) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(Services.Glyphs.FontFamily),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
