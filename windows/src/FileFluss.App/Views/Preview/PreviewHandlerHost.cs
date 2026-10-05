// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FileFluss.App.Views.Preview;

/// <summary>
/// Hosts a shell preview handler — the component behind Explorer's preview pane — so Quick
/// Look shows PDFs, Office documents and whatever else the user has handlers for, exactly
/// as Explorer would. Handlers are looked up per extension in the registry.
/// </summary>
public sealed class PreviewHandlerHost : HwndHost
{
    private const string PreviewHandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private nint _child;
    private IPreviewHandler? _handler;
    private string? _pendingPath;

    /// <summary>True when Windows has a preview handler registered for the file's extension.</summary>
    public static bool CanPreview(string path) => HandlerClsid(Path.GetExtension(path)) is not null;

    public void Show(string path)
    {
        _pendingPath = path;
        if (_child != 0)
        {
            Load();
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _child = CreateWindowEx(0, "static", string.Empty, WsChild | WsVisible | WsClipChildren, 0, 0, 100, 100, hwndParent.Handle, 0, 0, 0);
        if (_pendingPath is not null)
        {
            Dispatcher.BeginInvoke(Load);
        }

        return new HandleRef(this, _child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
        _child = 0;
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Resize();
    }

    private void Load()
    {
        Unload();
        if (_pendingPath is not { } path || HandlerClsid(Path.GetExtension(path)) is not { } clsid)
        {
            return;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(clsid);
            if (type is null || Activator.CreateInstance(type) is not IPreviewHandler handler)
            {
                return;
            }

            if (handler is IInitializeWithFile withFile)
            {
                withFile.Initialize(path, 0);
            }
            else if (handler is IInitializeWithStream withStream)
            {
                var stream = new ManagedIStream(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                withStream.Initialize(stream, 0);
            }
            else
            {
                Marshal.FinalReleaseComObject(handler);
                return;
            }

            _handler = handler;
            var rect = ClientRect();
            _handler.SetWindow(_child, ref rect);
            _handler.DoPreview();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            Unload();
        }
    }

    private void Resize()
    {
        if (_handler is null || _child == 0)
        {
            return;
        }

        var rect = ClientRect();
        try
        {
            _handler.SetRect(ref rect);
        }
        catch (COMException)
        {
        }
    }

    private NativeRect ClientRect()
    {
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return new NativeRect { Right = (int)(ActualWidth * scale), Bottom = (int)(ActualHeight * scale) };
    }

    private void Unload()
    {
        if (_handler is null)
        {
            return;
        }

        try
        {
            _handler.Unload();
        }
        catch (COMException)
        {
        }

        Marshal.FinalReleaseComObject(_handler);
        _handler = null;
    }

    private static Guid? HandlerClsid(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        static string? Read(string keyPath)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(keyPath);
            return key?.GetValue(null) as string;
        }

        var value = Read($@"{extension}\ShellEx\{PreviewHandlerKey}");
        if (value is null && Read(extension) is { } progId)
        {
            value = Read($@"{progId}\ShellEx\{PreviewHandlerKey}");
        }

        value ??= Read($@"SystemFileAssociations\{extension}\ShellEx\{PreviewHandlerKey}");
        return Guid.TryParse(value, out var clsid) ? clsid : null;
    }

    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(nint hwnd, ref NativeRect rect);

        void SetRect(ref NativeRect rect);

        void DoPreview();

        void Unload();

        void SetFocus();

        void QueryFocus(out nint hwnd);

        [PreserveSig]
        int TranslateAccelerator(nint message);
    }

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(System.Runtime.InteropServices.ComTypes.IStream stream, uint mode);
    }

    /// <summary>A read-only IStream over a .NET stream, for handlers that want a stream.</summary>
    private sealed class ManagedIStream(Stream stream) : System.Runtime.InteropServices.ComTypes.IStream
    {
        public void Read(byte[] pv, int cb, nint pcbRead)
        {
            var read = stream.Read(pv, 0, cb);
            if (pcbRead != 0)
            {
                Marshal.WriteInt32(pcbRead, read);
            }
        }

        public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
        {
            var position = stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != 0)
            {
                Marshal.WriteInt64(plibNewPosition, position);
            }
        }

        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
            => pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG { cbSize = stream.Length, type = 2 };

        public void Clone(out System.Runtime.InteropServices.ComTypes.IStream ppstm) => throw new NotSupportedException();

        public void Commit(int grfCommitFlags)
        {
        }

        public void CopyTo(System.Runtime.InteropServices.ComTypes.IStream pstm, long cb, nint pcbRead, nint pcbWritten) => throw new NotSupportedException();

        public void LockRegion(long libOffset, long cb, int dwLockType)
        {
        }

        public void Revert()
        {
        }

        public void SetSize(long libNewSize) => throw new NotSupportedException();

        public void UnlockRegion(long libOffset, long cb, int dwLockType)
        {
        }

        public void Write(byte[] pv, int cb, nint pcbWritten) => throw new NotSupportedException();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
}
