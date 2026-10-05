// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using FileFluss.Core.VirtualFiles;
using FileFluss.Native.VirtualFiles;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace FileFluss.Native.Tests.Support;

/// <summary>The receiving side of virtual files: what Explorer does with them, by hand and through the Shell itself.</summary>
internal static class ShellReceiver
{
    public const int DropEffectCopy = 1;
    public const int MkLButton = 1;

    private static readonly Guid BhidSfUiObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");
    private static readonly Guid IidIDropTarget = new("00000122-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>Reads the descriptor and every file's contents the way a drop target does, writing them under <paramref name="folder"/>.</summary>
    public static IReadOnlyList<FileGroupDescriptor.Descriptor> Extract(ComTypes.IDataObject data, string folder)
    {
        var format = new ComTypes.FORMATETC
        {
            cfFormat = VirtualFileDataObject.CfFileGroupDescriptorW,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };
        data.GetData(ref format, out var medium);
        var bytes = Ole.FromHGlobal(medium.unionmember);
        Ole.ReleaseStgMedium(ref medium);

        var descriptors = FileGroupDescriptor.Parse(bytes);
        for (var i = 0; i < descriptors.Count; i++)
        {
            var path = Path.Combine(folder, descriptors[i].Name);
            if (descriptors[i].IsDirectory)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var contents = new ComTypes.FORMATETC
            {
                cfFormat = VirtualFileDataObject.CfFileContents,
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = i,
                tymed = ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL,
            };
            data.GetData(ref contents, out var streamMedium);
            Assert.Equal(ComTypes.TYMED.TYMED_ISTREAM, streamMedium.tymed);
            var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(streamMedium.unionmember);
            var count = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                using var output = File.Create(path);
                var buffer = new byte[3]; // tiny on purpose: many reads per file
                while (true)
                {
                    stream.Read(buffer, buffer.Length, count);
                    var read = Marshal.ReadInt32(count);
                    if (read == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(count);
                Marshal.ReleaseComObject(stream);
                Ole.ReleaseStgMedium(ref streamMedium);
            }
        }

        return descriptors;
    }

    /// <summary>The Shell's own drop target for a file-system folder — what Explorer uses when you drop or paste into it.</summary>
    public static IDropTarget? FolderDropTarget(string folder)
    {
        if (SHCreateItemFromParsingName(folder, IntPtr.Zero, IidIShellItem, out var item) < 0 || item is null)
        {
            return null;
        }

        try
        {
            return item.BindToHandler(IntPtr.Zero, BhidSfUiObject, IidIDropTarget, out var target) < 0 ? null : (IDropTarget)Marshal.GetObjectForIUnknown(target);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointL
    {
        public int X;
        public int Y;
    }

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig]
        int DragEnter(ComTypes.IDataObject dataObject, int keyState, PointL point, ref int effect);

        [PreserveSig]
        int DragOver(int keyState, PointL point, ref int effect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop(ComTypes.IDataObject dataObject, int keyState, PointL point, ref int effect);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindContext, in Guid handler, in Guid riid, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid riid, out IShellItem item);
}

/// <summary>Saves and restores whatever the user had on the clipboard around a test that has to use it.</summary>
internal sealed class ClipboardSnapshot
{
    private static readonly HashSet<uint> GdiFormats = [2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E];
    private readonly List<(uint Format, byte[] Bytes)> _formats = [];

    private ClipboardSnapshot()
    {
    }

    public static ClipboardSnapshot Take()
    {
        var snapshot = new ClipboardSnapshot();
        if (!OpenClipboard(IntPtr.Zero))
        {
            return snapshot;
        }

        try
        {
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                if (GdiFormats.Contains(format) || format is >= 0x300 and <= 0x3FF)
                {
                    continue;
                }

                var handle = GetClipboardData(format);
                if (handle != IntPtr.Zero && GlobalSize(handle) > 0)
                {
                    snapshot._formats.Add((format, Ole.FromHGlobal(handle)));
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        return snapshot;
    }

    /// <summary>Empties the clipboard and puts the saved formats back.</summary>
    public void Restore()
    {
        // SetClipboardData needs an owner window; a message-only one will do.
        var window = CreateWindowExW(0, "STATIC", string.Empty, 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            for (var attempt = 0; attempt < 20 && !OpenClipboard(window); attempt++)
            {
                Thread.Sleep(25);
            }

            try
            {
                EmptyClipboard();
                foreach (var (format, bytes) in _formats)
                {
                    var handle = Ole.ToHGlobal(bytes);
                    if (SetClipboardData(format, handle) == IntPtr.Zero)
                    {
                        _ = GlobalFree(handle);
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
