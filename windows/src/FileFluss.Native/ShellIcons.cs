// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FileFluss.Native;

/// <summary>System image list size, as SHGetImageList names them.</summary>
public enum ShellIconSize
{
    /// <summary>16 px at 100% scaling.</summary>
    Small = 1,

    /// <summary>32 px — the right source for 16-DIP rows at 200%.</summary>
    Large = 0,

    /// <summary>48 px.</summary>
    ExtraLarge = 2,

    /// <summary>256 px.</summary>
    Jumbo = 4,
}

/// <summary>
/// Explorer's own icons and type names, so a file looks the same in FileFluss as in File
/// Explorer — for cloud files too, which have no local path: an extension is enough for
/// the shell to answer with <c>SHGFI_USEFILEATTRIBUTES</c>.
/// </summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> ExtensionIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions whose icon lives in the file itself, so the extension alone can't answer.</summary>
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cpl", ".msc", ".appref-ms", ".scr",
    };

    public static bool NeedsPerFileIcon(string name) => PerFileExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// The system image list index for an item. <paramref name="path"/> is a real path when
    /// <paramref name="exists"/>, otherwise any name with the right extension.
    /// </summary>
    public static int IconIndex(string path, bool isDirectory, bool exists)
    {
        if (!exists || (!isDirectory && !NeedsPerFileIcon(path)))
        {
            var key = isDirectory ? "\\folder" : Path.GetExtension(path);
            return ExtensionIndexes.GetOrAdd(key, _ => QueryIndex(isDirectory ? "folder" : "file" + key, isDirectory, useAttributes: true));
        }

        return QueryIndex(path, isDirectory, useAttributes: false);
    }

    private static int QueryIndex(string path, bool isDirectory, bool useAttributes)
    {
        var info = default(ShFileInfo);
        var flags = ShgfiSysIconIndex | (useAttributes ? ShgfiUseFileAttributes : 0);
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        return result == 0 ? -1 : info.IconIndex;
    }

    /// <summary>An HICON from the system image list; the caller destroys it with <see cref="DestroyIcon"/>.</summary>
    public static nint IconFromIndex(int index, ShellIconSize size)
    {
        if (index < 0)
        {
            return 0;
        }

        var iid = typeof(IImageList).GUID;
        if (SHGetImageList((int)size, ref iid, out var list) != 0 || list is null)
        {
            return 0;
        }

        return list.GetIcon(index, IldTransparent, out var icon) == 0 ? icon : 0;
    }

    /// <summary>Explorer's "Type" column text: "PNG File", "Microsoft Word Document", "File folder".</summary>
    public static string TypeName(string name, bool isDirectory)
    {
        var key = isDirectory ? "\\folder" : Path.GetExtension(name);
        return TypeNames.GetOrAdd(key, k =>
        {
            var info = default(ShFileInfo);
            var probe = isDirectory ? "folder" : "file" + k;
            var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
            SHGetFileInfo(probe, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiTypeName | ShgfiUseFileAttributes);
            if (!string.IsNullOrWhiteSpace(info.TypeName))
            {
                return info.TypeName;
            }

            return k.Length > 1 ? k[1..].ToUpperInvariant() + " File" : "File";
        });
    }

    /// <summary>
    /// A thumbnail (or the icon, for files without one) as an HBITMAP with alpha; the caller
    /// frees it with <see cref="DeleteObject"/>. Zero when the shell has nothing.
    /// </summary>
    public static nint Thumbnail(string path, int pixelSize, bool thumbnailOnly)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, 0, ref iid, out var factory) != 0 || factory is null)
        {
            return 0;
        }

        try
        {
            var flags = SiigbfBiggerSizeOk | (thumbnailOnly ? SiigbfThumbnailOnly : 0);
            return factory.GetImage(new NativeSize(pixelSize, pixelSize), flags, out var bitmap) == 0 ? bitmap : 0;
        }
        catch (COMException)
        {
            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(nint icon);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint handle);

    private const uint ShgfiSysIconIndex = 0x4000;
    private const uint ShgfiTypeName = 0x400;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const int IldTransparent = 0x1;
    private const int SiigbfBiggerSizeOk = 0x1;
    private const int SiigbfThumbnailOnly = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(nint image, nint mask, out int index);
        [PreserveSig] int ReplaceIcon(int i, nint icon, out int index);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int i, nint image, nint mask);
        [PreserveSig] int AddMasked(nint image, int mask, out int index);
        [PreserveSig] int Draw(nint parameters);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out nint icon);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int list, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageList? imageList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);
}
