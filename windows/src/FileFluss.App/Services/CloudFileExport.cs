// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FileFluss.App.ViewModels;
using FileFluss.App.Views;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.VirtualFiles;
using FileFluss.Native.VirtualFiles;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Services;

/// <summary>
/// Cloud files out of FileFluss into Explorer, the desktop or any other app — by dragging, or
/// Ctrl+C and Paste. The Windows counterpart of the macOS file promise: the other app gets
/// "virtual files" (see <see cref="VirtualFileDataObject"/>) and FileFluss downloads each one
/// when the other app reads it. Downloads show in Transfers, where they can be cancelled;
/// Explorer shows its own copy dialog as well.
///
/// <para>Drops back into FileFluss don't use any of that: they see the in-app
/// <see cref="DragPayload"/> and run a normal transfer.</para>
/// </summary>
public sealed class CloudFileExport
{
    private readonly TransferCenter _transfers;
    private VirtualFileDataObject? _clipboard;

    public CloudFileExport(TransferCenter transfers)
    {
        _transfers = transfers;
        VirtualFileDataObject.Wait = WaitPumping;
    }

    /// <summary>Whether a panel's selection can go to other apps as virtual files: a connected cloud, not an offline index.</summary>
    public static bool CanExport(PanelViewModel panel)
        => panel.IsCloud && panel.Provider is { } provider && VirtualFileSource.CanProvideContents(provider) && panel.Account is not { IsEffectivelyOffline: true };

    // MARK: - Drag

    /// <summary>
    /// Runs the drag (modal, like WPF's DoDragDrop). The tree is listed only once the pointer
    /// reaches another app's window — a drag that stays inside FileFluss lists nothing — and
    /// the pointer shows "working in background" until it is ready.
    /// </summary>
    public void Drag(Window? owner, PanelViewModel panel, IReadOnlyList<CloudFileItem> items, DragPayload payload)
    {
        var data = Create(panel, items, forDrag: true);
        var reported = false;
        void Report(Exception failure)
        {
            if (!reported)
            {
                reported = true;
                ShowUnavailable(owner, failure);
            }
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        data.TreeUnavailable += failure => dispatcher.BeginInvoke(() => Report(failure));
        var source = data.Source;
        OleDragResult result;
        DragPayload.Outgoing = payload;
        try
        {
            result = OleDragDrop.DoDragDrop(
                data,
                VirtualFileDataObject.DropEffectCopy | VirtualFileDataObject.DropEffectMove,
                isBusy: () => source.IsTreeStarted && !source.TreeAsync().IsCompleted,
                pointerLeftProcess: () => _ = source.TreeAsync());
        }
        catch (COMException e)
        {
            SupportLogger.Shared.Log($"Drag failed: {e.Message}", "dragOut", LogLevel.Error);
            result = new OleDragResult(false, 0, false);
        }
        finally
        {
            DragPayload.Outgoing = null;
        }

        // Released over another app, which refused: the tree was too large or couldn't be listed.
        var refusedFailure = result is { Dropped: true, OverOtherProcess: true, Effect: 0 } ? data.TreeFailure : null;

        // An asynchronous extraction (Explorer) keeps the source alive until EndOperation;
        // anything else is over now.
        source.Retire();
        if (refusedFailure is not null)
        {
            Report(refusedFailure);
        }
    }

    // MARK: - Clipboard

    /// <summary>
    /// Puts cloud files on the Windows clipboard for Explorer's Paste (FileFluss's own paste
    /// keeps using its in-app clipboard). The tree is listed straight away in the background so
    /// a paste finds it ready.
    /// </summary>
    public void CopyToClipboard(Window? owner, PanelViewModel panel, IReadOnlyList<CloudFileItem> items)
    {
        ReleaseClipboard(clear: false);
        var data = Create(panel, items, forDrag: false);
        var dispatcher = Dispatcher.CurrentDispatcher;
        data.TreeUnavailable += failure => dispatcher.BeginInvoke(() => ShowUnavailable(owner, failure));
        _ = data.Source.TreeAsync();
        if (OleDragDrop.SetClipboard(data))
        {
            _clipboard = data;
        }
        else
        {
            data.Source.Retire();
        }
    }

    /// <summary>
    /// Lets go of what FileFluss put on the clipboard; with <paramref name="clear"/> the
    /// clipboard is emptied too if it still holds it (a cut, or quitting: virtual files can't
    /// outlive FileFluss).
    /// </summary>
    public void ReleaseClipboard(bool clear)
    {
        if (_clipboard is not { } data)
        {
            return;
        }

        _clipboard = null;
        if (clear)
        {
            OleDragDrop.ClearClipboardIfHolding(data);
        }

        data.Source.Retire();
    }

    /// <summary>True when FileFluss put cloud files on the clipboard and something else has replaced them since.</summary>
    public bool ClipboardWasReplaced => _clipboard is { } data && !OleDragDrop.IsOnClipboard(data);

    // MARK: - Helpers

    private VirtualFileDataObject Create(PanelViewModel panel, IReadOnlyList<CloudFileItem> items, bool forDrag)
    {
        var source = new VirtualFileSource(panel.Provider!, items, panel.Side);
        var dispatcher = Application.Current.Dispatcher;
        source.SessionStarted += progress => dispatcher.BeginInvoke(() => _transfers.Track(progress));
        return new VirtualFileDataObject(source) { HideFormatsWhenUnavailable = forDrag };
    }

    private static void ShowUnavailable(Window? owner, Exception failure)
    {
        var message = failure switch
        {
            VirtualFileTreeException { Problem: VirtualFileTreeProblem.TooManyItems } =>
                L("This selection contains more than {0} files and folders, too many to hand over to Explorer or another app. Open a local folder in the other panel and copy the selection there with FileFluss.", VirtualFileTree.MaxEntries),
            VirtualFileTreeException =>
                L("Some files in this selection are nested too deeply to hand over to Explorer or another app. Open a local folder in the other panel and copy the selection there with FileFluss."),
            OperationCanceledException => null,
            _ => L("The files couldn't be prepared for the other app: {0}", failure.Message),
        };

        if (message is not null)
        {
            MessageDialog.Error(owner, L("Couldn't Hand Over Files"), message);
        }
    }

    /// <summary>
    /// How a call from another app waits on the UI thread for the tree listing (a paste right
    /// after Ctrl+C on a big folder): the window keeps repainting and shows a wait cursor
    /// instead of freezing.
    /// </summary>
    private static bool WaitPumping(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null)
        {
            return ((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeout);
        }

        var frame = new DispatcherFrame();
        void Stop() => dispatcher.BeginInvoke(() => frame.Continue = false);
        using var timer = new Timer(_ => Stop(), null, timeout, Timeout.InfiniteTimeSpan);
        task.ContinueWith(_ => Stop(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Dispatcher.PushFrame(frame);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher processing is suspended (mid-layout): block instead.
            return ((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeout);
        }
        finally
        {
            Mouse.OverrideCursor = cursor;
        }

        return task.IsCompleted;
    }
}
