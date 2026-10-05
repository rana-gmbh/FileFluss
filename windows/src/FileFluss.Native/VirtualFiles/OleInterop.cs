// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace FileFluss.Native.VirtualFiles;

/// <summary>
/// IDataObjectAsyncCapability (formerly IAsyncOperation). A data object that offers it lets
/// Explorer extract on a background thread after the drop: DoDragDrop returns at once and the
/// source's UI keeps running while files download. The vtable order is fixed by shobjidl.h.
/// </summary>
[ComImport]
[Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    [PreserveSig]
    int SetAsyncMode(int fDoOpAsync);

    [PreserveSig]
    int GetAsyncMode(out int pfIsOpAsync);

    [PreserveSig]
    int StartOperation(IntPtr pbcReserved);

    [PreserveSig]
    int InOperation(out int pfInAsyncOp);

    [PreserveSig]
    int EndOperation(int hResult, IntPtr pbcReserved, uint dwEffects);
}

[ComImport]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag(int fEscapePressed, int grfKeyState);

    [PreserveSig]
    int GiveFeedback(int dwEffect);
}

/// <summary>Win32/OLE declarations and HRESULTs shared by the virtual-file classes.</summary>
internal static class Ole
{
    public const int SOk = 0;
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int EFail = unchecked((int)0x80004005);
    public const int EUnexpected = unchecked((int)0x8000FFFF);
    public const int DvEFormatEtc = unchecked((int)0x80040064);
    public const int DvELindex = unchecked((int)0x80040068);
    public const int DvETymed = unchecked((int)0x80040069);
    public const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    public const int DataSSameFormatEtc = 0x00040130;
    public const int ClipbrdECantOpen = unchecked((int)0x800401D0);
    public const int StgEInvalidFunction = unchecked((int)0x80030001);
    public const int StgEAccessDenied = unchecked((int)0x80030005);

    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED): Explorer treats it as the user cancelling, without an error dialog.</summary>
    public const int HrCancelled = unchecked((int)0x800704C7);

    public const int DragDropSDrop = 0x00040100;
    public const int DragDropSCancel = 0x00040101;
    public const int DragDropSUseDefaultCursors = 0x00040102;

    public const int MkLButton = 0x0001;

    public static readonly Guid IidIStream = new("0000000C-0000-0000-C000-000000000046");
    public static readonly Guid IidIMarshal = new("00000003-0000-0000-C000-000000000046");
    public static readonly Guid IidIAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    public static readonly Guid IidIProvideClassInfo = new("B196B283-BAB4-101A-B69C-00AA00341D07");
    public static readonly Guid IidIManagedObject = new("C3FCC19E-A970-11D2-8B5A-00A0C9B7C9C4");
    public static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>
    /// What our COM objects refuse in QueryInterface. IMarshal/IAgileObject: see
    /// <see cref="MtaMarshaller"/>. IDispatch/IProvideClassInfo/IManagedObject: a .NET CCW
    /// answers their type-info calls by exporting a type library, which modern .NET can't, and
    /// a .NET Framework receiver (WinForms or WPF apps on .NET Framework) then failed to wrap
    /// our stream at all ("Typelib export: Type library is not registered"). Nothing here is
    /// scriptable, so none of them is needed.
    /// </summary>
    public static bool Refused(Guid iid)
        => iid == IidIMarshal || iid == IidIAgileObject || iid == IidIProvideClassInfo || iid == IidIManagedObject || iid == IidIDispatch;

    public static short RegisterFormat(string name) => unchecked((short)RegisterClipboardFormatW(name));

    /// <summary>A movable HGLOBAL holding <paramref name="bytes"/>; the receiver frees it (ReleaseStgMedium).</summary>
    public static IntPtr ToHGlobal(ReadOnlySpan<byte> bytes)
    {
        var handle = GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, Math.Max(1, bytes.Length));
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException();
        }

        var pointer = GlobalLock(handle);
        try
        {
            unsafe
            {
                bytes.CopyTo(new Span<byte>((void*)pointer, bytes.Length));
            }
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    public static byte[] FromHGlobal(IntPtr handle)
    {
        var size = (int)GlobalSize(handle);
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Whether the calling thread is in a single-threaded apartment (a UI thread).</summary>
    public static bool OnSta()
        => CoGetApartmentType(out var type, out _) == SOk && type is 0 /* STA */ or 3 /* main STA */;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterClipboardFormatW(string format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalSize(IntPtr handle);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [DllImport("ole32.dll")]
    private static extern int CoGetApartmentType(out int type, out int qualifier);

    [DllImport("ole32.dll")]
    public static extern int CoMarshalInterThreadInterfaceInStream(in Guid riid, IntPtr unknown, out IntPtr stream);

    [DllImport("ole32.dll")]
    public static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, in Guid riid, out IntPtr result);

    [DllImport("shell32.dll")]
    public static extern int SHCreateStdEnumFmtEtc(uint count, [In] ComTypes.FORMATETC[] formats, out ComTypes.IEnumFORMATETC enumerator);

    [DllImport("ole32.dll")]
    public static extern int DoDragDrop(ComTypes.IDataObject dataObject, IDropSource dropSource, int okEffects, out int effect);

    [DllImport("ole32.dll")]
    public static extern int OleSetClipboard(ComTypes.IDataObject? dataObject);

    [DllImport("ole32.dll")]
    public static extern int OleIsCurrentClipboard(ComTypes.IDataObject dataObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr cursor);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }
}

/// <summary>
/// Hands out interface pointers whose calls run in the multithreaded apartment.
///
/// <para>A data object put on the clipboard or into DoDragDrop is marshalled from the UI
/// thread, so every call Explorer makes on it is queued to FileFluss's UI thread. That is fine
/// for the quick calls (formats, the descriptor), but a stream's Read blocks until the cloud
/// download is there. Streams are therefore registered in the MTA, from this dedicated thread,
/// and the UI thread passes on a proxy: COM marshals a proxy as a reference to the original
/// apartment, so Explorer's reads land on RPC worker threads and the UI never waits on the
/// network. (The thread also keeps the MTA alive for as long as those stubs exist.)</para>
/// </summary>
internal static class MtaMarshaller
{
    private static readonly BlockingCollection<Action> Work = [];

    static MtaMarshaller()
    {
        var thread = new Thread(() =>
        {
            foreach (var action in Work.GetConsumingEnumerable())
            {
                action();
            }
        })
        {
            IsBackground = true,
            Name = "FileFluss virtual files (MTA)",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>
    /// A pointer, valid in the calling apartment, to <paramref name="unknown"/> as registered
    /// in the MTA. Takes over the caller's reference to <paramref name="unknown"/> only on success.
    /// </summary>
    public static IntPtr ProxyFromMta(IntPtr unknown, Guid iid)
    {
        var marshalled = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() =>
        {
            var hr = Ole.CoMarshalInterThreadInterfaceInStream(iid, unknown, out var stream);
            if (hr < 0)
            {
                marshalled.SetException(Marshal.GetExceptionForHR(hr)!);
            }
            else
            {
                marshalled.SetResult(stream);
            }
        });

        var stream = marshalled.Task.GetAwaiter().GetResult();
        Marshal.ThrowExceptionForHR(Ole.CoGetInterfaceAndReleaseStream(stream, iid, out var proxy));
        Marshal.Release(unknown);
        return proxy;
    }
}
