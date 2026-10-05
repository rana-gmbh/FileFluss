// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using FileFluss.Core.Settings;

namespace FileFluss.App.Services;

/// <summary>
/// Remembers a secondary window's size and position across launches (macOS
/// <c>WindowFrameAutosaver</c>), in <see cref="AppSettings.WindowFrames"/> under a key per window.
/// A frame that would land off every screen is ignored and the window centres on its owner.
/// </summary>
public static class WindowFrames
{
    /// <summary>Restores the saved frame (before the window is shown) and saves it again on close.</summary>
    public static void Attach(Window window, AppSettings settings, string key)
    {
        if (settings.WindowFrames.TryGetValue(key, out var frame) && IsOnScreen(frame))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = frame.Left;
            window.Top = frame.Top;
            window.Width = Math.Max(window.MinWidth, frame.Width);
            window.Height = Math.Max(window.MinHeight, frame.Height);
            if (frame.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Closing += (_, _) =>
        {
            var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            if (bounds.IsEmpty || double.IsNaN(bounds.Left))
            {
                return;
            }

            // A fresh dictionary, so the settings store sees the change and saves it.
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
