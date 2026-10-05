// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using FileFluss.Core.Settings;

namespace FileFluss.App.Services;

/// <summary>
/// Remembers where a secondary window was (macOS <c>WindowFrameAutosaver</c>): restored when it
/// opens again — if that spot is still on a screen — and saved as it closes.
/// </summary>
public static class WindowFrames
{
    /// <summary>Restores the window's last frame under <paramref name="key"/> and saves it when the window closes.</summary>
    public static void Remember(Window window, string key, AppSettings settings)
    {
        if (settings.WindowFrames.TryGetValue(key, out var frame) && IsOnScreen(frame))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = frame.Left;
            window.Top = frame.Top;
            if (window.SizeToContent == SizeToContent.Manual)
            {
                window.Width = Math.Max(window.MinWidth, frame.Width);
                window.Height = Math.Max(window.MinHeight, frame.Height);
            }

            if (frame.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }
        }

        window.Closing += (_, _) =>
        {
            var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
            if (bounds.IsEmpty)
            {
                return;
            }

            // A new dictionary so the settings store sees the change and saves it.
            settings.WindowFrames = new Dictionary<string, WindowPlacement>(settings.WindowFrames)
            {
                [key] = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized),
            };
        };
    }

    private static bool IsOnScreen(WindowPlacement frame)
        => SystemParameters.VirtualScreenLeft <= frame.Left + 100 &&
           frame.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
           SystemParameters.VirtualScreenTop <= frame.Top + 20 &&
           frame.Top + 20 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
}
