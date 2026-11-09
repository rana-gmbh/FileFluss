// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using FileFluss.Core.Diagnostics;

namespace FileFluss.App.Services;

/// <summary>
/// Debug builds log how long startup took to each milestone (first window, home listed, …),
/// counted from process start, into the support log and debug.log. Release builds compile the
/// calls away.
/// </summary>
internal static class StartupTrace
{
    [Conditional("DEBUG")]
    public static void Mark(string milestone)
    {
        var started = Process.GetCurrentProcess().StartTime;
        SupportLogger.Shared.Log($"Startup: {milestone} — {(DateTime.Now - started).TotalMilliseconds:F0} ms after process start", "Startup");
    }
}
