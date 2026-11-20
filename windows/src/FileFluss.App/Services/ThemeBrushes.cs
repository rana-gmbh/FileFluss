// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FileFluss.App.Services;

/// <summary>
/// The few brushes the Fluent theme doesn't provide: Explorer's translucent accent selection,
/// readable accent text on both light and dark backgrounds. Rebuilt whenever Windows changes
/// its theme or accent colour.
/// </summary>
public static class ThemeBrushes
{
    public static bool IsDark { get; private set; }

    public static void Install(Application application)
    {
        Apply(application);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                application.Dispatcher.BeginInvoke(() => Apply(application));
            }
        };
    }

    public static void Apply(Application application)
    {
        IsDark = ReadIsDark();
        var accent = SystemColors.AccentColor;
        var resources = application.Resources;
        resources["RowSelectedBrush"] = Frozen(Color.FromArgb(IsDark ? (byte)0x55 : (byte)0x40, accent.R, accent.G, accent.B));
        resources["RowSelectedInactiveBrush"] = Frozen(IsDark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        resources["RowFocusBrush"] = Frozen(Color.FromArgb(0x70, accent.R, accent.G, accent.B));
        resources["AccentTextBrush"] = Frozen(IsDark ? SystemColors.AccentColorLight2 : SystemColors.AccentColorDark1);
        resources["ActivePanelBrush"] = Frozen(accent);
        resources["ConnectedBrush"] = Frozen(Color.FromRgb(0x2E, 0xB8, 0x4F));
        resources["DisconnectedBrush"] = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));
        resources["DangerBrush"] = Frozen(IsDark ? Color.FromRgb(0xFF, 0x99, 0xA4) : Color.FromRgb(0xC4, 0x2B, 0x1C));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static bool ReadIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
