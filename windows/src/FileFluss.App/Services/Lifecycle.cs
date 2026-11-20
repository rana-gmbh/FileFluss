// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Windows;
using FileFluss.Core.Diagnostics;

namespace FileFluss.App.Services;

/// <summary>
/// Leaves a trace of how FileFluss ended, in the support log (and the debug log): a closed
/// window, Windows ending the session or an installer's Restart Manager, an explicit exit, or
/// — when none of these appear before "process exiting" — something that ended the process
/// from outside. Without it an exit right after launch looks the same whatever caused it.
/// </summary>
internal static class Lifecycle
{
    public static void Install(Application application)
    {
        application.SessionEnding += (_, e) =>
            SupportLogger.Shared.Log($"Windows is ending the session ({e.ReasonSessionEnding})", "App", LogLevel.Notice);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SupportLogger.Shared.Log("Process exiting", "App");
    }

    /// <summary>Logs that <paramref name="what"/> is closing and which FileFluss code asked for it, if any.</summary>
    public static void LogClosing(string what)
    {
        var caller = Initiator(new StackTrace(1, fNeedFileInfo: false));
        SupportLogger.Shared.Log(caller is null ? $"{what} closing (from Windows: title bar, Alt+F4, taskbar or the system)" : $"{what} closing (requested by {caller})", "App");
    }

    /// <summary>The innermost FileFluss method on the stack outside this class and the window's own Closing handler; null when only the framework is there.</summary>
    internal static string? Initiator(StackTrace stack)
    {
        foreach (var frame in stack.GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (type?.FullName is not { } name || !name.StartsWith("FileFluss.", StringComparison.Ordinal) || type == typeof(Lifecycle))
            {
                continue;
            }

            // The window's own Closing handler is how we got here, and Main runs the message
            // loop that delivers a click on the title bar — neither is who asked.
            if (method!.Name.Contains("OnClosing", StringComparison.Ordinal) || method.Name == "Main")
            {
                continue;
            }

            return $"{type.Name}.{method.Name}";
        }

        return null;
    }
}
