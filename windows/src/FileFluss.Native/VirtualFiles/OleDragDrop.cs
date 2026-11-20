// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace FileFluss.Native.VirtualFiles;

/// <summary>How a drag ended.</summary>
/// <param name="Dropped">Released over a target (not cancelled with Esc).</param>
/// <param name="Effect">The DROPEFFECT the target reported (0 = refused).</param>
/// <param name="OverOtherProcess">The pointer was over another app's window when released.</param>
public sealed record OleDragResult(bool Dropped, int Effect, bool OverOtherProcess);

/// <summary>
/// OLE drag and drop and the clipboard for a data object WPF must not wrap: WPF's own
/// DoDragDrop and Clipboard put the object inside a WPF DataObject, which hides
/// IDataObjectAsyncCapability from Explorer.
/// </summary>
public static class OleDragDrop
{
    /// <summary>
    /// Runs a modal OLE drag (call on the UI thread, with the left button down).
    /// <paramref name="isBusy"/> shows the "working in background" pointer while true;
    /// <paramref name="pointerLeftProcess"/> runs once, the first time the pointer is over
    /// another app's window — the moment to start preparing what only another app needs.
    /// </summary>
    public static OleDragResult DoDragDrop(ComTypes.IDataObject data, int allowedEffects, Func<bool>? isBusy = null, Action? pointerLeftProcess = null)
    {
        var source = new DropSource(isBusy, pointerLeftProcess);
        var hr = Ole.DoDragDrop(data, source, allowedEffects, out var effect);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return new OleDragResult(hr == Ole.DragDropSDrop, hr == Ole.DragDropSDrop ? effect : 0, source.ReleasedOverOtherProcess);
    }

    /// <summary>Puts <paramref name="data"/> on the clipboard (retrying while another app holds it open).</summary>
    public static bool SetClipboard(ComTypes.IDataObject data)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var hr = Ole.OleSetClipboard(data);
            if (hr >= 0)
            {
                return true;
            }

            if (hr != Ole.ClipbrdECantOpen)
            {
                return false;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    /// <summary>Whether <paramref name="data"/> is still what the clipboard holds.</summary>
    public static bool IsOnClipboard(ComTypes.IDataObject data) => Ole.OleIsCurrentClipboard(data) == Ole.SOk;

    /// <summary>
    /// Empties the clipboard if it still holds <paramref name="data"/>. Used when FileFluss
    /// quits: OLE would otherwise flush the descriptor without the contents behind it, leaving
    /// a paste that can only fail.
    /// </summary>
    public static void ClearClipboardIfHolding(ComTypes.IDataObject data)
    {
        if (IsOnClipboard(data))
        {
            _ = Ole.OleSetClipboard(null);
        }
    }

    [ComVisible(true)]
    private sealed class DropSource(Func<bool>? isBusy, Action? pointerLeftProcess) : IDropSource
    {
        private static readonly IntPtr AppStartingCursor = Ole.LoadCursorW(IntPtr.Zero, 32650 /* IDC_APPSTARTING */);
        private bool _left;

        public bool ReleasedOverOtherProcess { get; private set; }

        public int QueryContinueDrag(int fEscapePressed, int grfKeyState)
        {
            if (fEscapePressed != 0)
            {
                return Ole.DragDropSCancel;
            }

            var outside = PointerOverOtherProcess();
            if (outside && !_left)
            {
                _left = true;
                pointerLeftProcess?.Invoke();
            }

            if ((grfKeyState & Ole.MkLButton) == 0)
            {
                ReleasedOverOtherProcess = outside;
                return Ole.DragDropSDrop;
            }

            return Ole.SOk;
        }

        public int GiveFeedback(int dwEffect)
        {
            if (dwEffect != 0 && isBusy?.Invoke() == true)
            {
                Ole.SetCursor(AppStartingCursor);
                return Ole.SOk;
            }

            return Ole.DragDropSUseDefaultCursors;
        }

        private static bool PointerOverOtherProcess()
        {
            if (!Ole.GetCursorPos(out var point))
            {
                return false;
            }

            var window = Ole.WindowFromPoint(point);
            if (window == IntPtr.Zero)
            {
                return false;
            }

            _ = Ole.GetWindowThreadProcessId(window, out var processId);
            return processId != 0 && processId != (uint)Environment.ProcessId;
        }
    }
}
