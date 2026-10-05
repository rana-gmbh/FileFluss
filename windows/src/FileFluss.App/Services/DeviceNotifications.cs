// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FileFluss.App.Services;

/// <summary>
/// Drive arrival and removal (WM_DEVICECHANGE) — the Windows side of the macOS DriveMonitor.
/// Debounced: plugging in one USB stick sends several messages.
/// </summary>
public static class DeviceNotifications
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    public static void Register(Window window, Action onChange)
    {
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            onChange();
        };

        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == WmDeviceChange && (wParam == DbtDeviceArrival || wParam == DbtDeviceRemoveComplete))
            {
                debounce.Stop();
                debounce.Start();
            }

            return 0;
        });
    }
}
