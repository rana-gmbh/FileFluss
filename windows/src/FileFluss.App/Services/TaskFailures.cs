// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Diagnostics;

namespace FileFluss.App.Services;

/// <summary>Fire-and-forget without losing failures.</summary>
internal static class TaskFailures
{
    /// <summary>
    /// Lets <paramref name="task"/> run on its own; a failure is written to the error log at
    /// once instead of surfacing much later (if ever) as an unobserved task exception.
    /// Cancellation is not a failure.
    /// </summary>
    public static async void Forget(this Task task, string context)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            CrashLog.Write(context, e);
        }
    }

    /// <summary>
    /// For something the user started (a menu item, a button): a failure is logged and shown
    /// as an error under <paramref name="title"/>, instead of ending in the last-resort
    /// handler, which keeps FileFluss running but tells the user nothing.
    /// </summary>
    public static async void ReportFailure(this Task task, string title, Window? owner)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            ShowFailure(e, title, owner);
        }
    }

    /// <summary>Logs <paramref name="e"/> and tells the user, when the app is still there to tell.</summary>
    public static void ShowFailure(Exception e, string title, Window? owner)
    {
        SupportLogger.Shared.Log($"{title} failed: {e.GetType().Name}: {e.Message}", "App", LogLevel.Error);
        if (Application.Current?.Dispatcher is { HasShutdownStarted: false } dispatcher && dispatcher.CheckAccess())
        {
            MessageDialog.Error(owner, title, e.Message);
        }
    }
}
