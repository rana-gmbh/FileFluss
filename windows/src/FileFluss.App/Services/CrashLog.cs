// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Text;
using System.Windows;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Settings;

namespace FileFluss.App.Services;

/// <summary>
/// Last-resort error handling (from the NetFluss Windows port): unhandled errors are written
/// to <c>%LOCALAPPDATA%\FileFluss\logs\errors.log</c>, and UI-thread errors are survived
/// rather than fatal wherever WPF allows — a broken context menu must not take two open
/// panels and a running transfer down with it.
/// </summary>
internal static class CrashLog
{
    private const long MaximumSize = 512 * 1024;

    internal static string LogPath { get; } = Path.Combine(SettingsStore.DataDirectory, "logs", "errors.log");

    internal static void Install(Application application)
    {
        application.DispatcherUnhandledException += (_, e) =>
        {
            Write("UI", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                Write(e.IsTerminating ? "fatal" : "background", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("task", e.Exception);
            e.SetObserved();
        };
    }

    internal static void Write(string context, Exception exception)
    {
        SupportLogger.Shared.Log($"[{context}] {exception.GetType().Name}: {exception.Message}", "Crash", LogLevel.Error);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumSize)
            {
                File.Move(LogPath, LogPath + ".1", overwrite: true);
            }

            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("u"))
                .Append(" [").Append(context).Append("] ")
                .AppendLine(LogRedactor.Redact(exception.ToString()))
                .AppendLine();
            File.AppendAllText(LogPath, entry.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
