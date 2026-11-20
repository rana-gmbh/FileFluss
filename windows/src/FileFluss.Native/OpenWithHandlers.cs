// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FileFluss.Native;

/// <summary>
/// One app the shell offers to open a file type with, as Explorer's "Open with" submenu
/// lists it. <see cref="Id"/> is the shell's own name for the handler (an executable's path,
/// or a packaged app's identity) — the same app answers with the same id for every type it
/// opens, which is what lets a multi-selection be intersected.
/// </summary>
/// <param name="Id">IAssocHandler::GetName.</param>
/// <param name="Name">IAssocHandler::GetUIName — "Notepad", "Photos".</param>
/// <param name="IconLocation">IAssocHandler::GetIconLocation: a file, or an indirect "@{…}" resource of a packaged app.</param>
/// <param name="IconIndex">The icon's index in <paramref name="IconLocation"/>.</param>
public sealed record OpenWithHandler(string Id, string Name, string? IconLocation, int IconIndex);

/// <summary>
/// The apps registered to open a file type (<c>SHAssocEnumHandlers</c>), their icons, and
/// opening files in one of them (<c>IAssocHandler::Invoke</c>) — the Windows side of macOS
/// <c>OpenWithCandidates</c>, which asks LaunchServices the same questions.
///
/// <para>Shell handlers are apartment-bound COM objects, so nothing here keeps one: each call
/// enumerates, copies out what it needs, and releases them. Call from an STA thread (the UI
/// thread, or a worker set to STA as the icon threads are).</para>
/// </summary>
public static class OpenWithHandlers
{
    /// <summary>
    /// The apps Explorer recommends for <paramref name="extension"/> (".txt"), in the shell's
    /// order (by name). Empty when nothing is registered, or for a name without an extension.
    /// </summary>
    public static IReadOnlyList<OpenWithHandler> ForExtension(string extension)
    {
        var result = new List<OpenWithHandler>();
        if (!IsExtension(extension))
        {
            return result;
        }

        Enumerate(extension, recommendedOnly: true, handler =>
        {
            if (Describe(handler) is { } info && !result.Exists(r => string.Equals(r.Id, info.Id, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(info);
            }

            return false;
        });
        return result;
    }

    /// <summary>
    /// The id of the app a double-click opens <paramref name="extension"/> with, when it is
    /// among <paramref name="handlers"/>. The shell lists handlers by name, not default first,
    /// so the association itself is asked: its executable, else its friendly app name (a
    /// packaged default such as Photos has no executable of its own). A type with no default,
    /// or one no handler matches, answers null.
    /// </summary>
    public static string? DefaultHandlerId(string extension, IReadOnlyList<OpenWithHandler> handlers)
    {
        if (handlers.Count == 0 || !IsExtension(extension))
        {
            return null;
        }

        var friendly = AssocString(extension, AssocStrFriendlyAppName);
        var executable = AssocString(extension, AssocStrExecutable);
        foreach (var handler in handlers)
        {
            if ((executable is not null && string.Equals(handler.Id, executable, StringComparison.OrdinalIgnoreCase)) ||
                (friendly is not null && string.Equals(handler.Name, friendly, StringComparison.CurrentCultureIgnoreCase)))
            {
                return handler.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens <paramref name="paths"/> in the app <paramref name="handlerId"/>, found among the
    /// handlers for <paramref name="extension"/>. False when that app is no longer registered
    /// or refused the files — the caller then says so (or tries <see cref="LaunchExecutable"/>).
    /// </summary>
    public static bool Invoke(string extension, string handlerId, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !IsExtension(extension))
        {
            return false;
        }

        var invoked = false;
        Enumerate(extension, recommendedOnly: false, handler =>
        {
            if (handler.GetName(out var name) != 0 || !string.Equals(name, handlerId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var dataObject = DataObjectFor(paths);
            if (dataObject is null)
            {
                return true;
            }

            try
            {
                invoked = handler.Invoke(dataObject) >= 0;
            }
            catch (COMException)
            {
                invoked = false;
            }
            finally
            {
                Marshal.ReleaseComObject(dataObject);
            }

            return true;
        });
        return invoked;
    }

    /// <summary>
    /// The fallback when <see cref="Invoke"/> fails for a classic app: its executable started
    /// with the files as arguments. False when the id is not an executable on disk.
    /// </summary>
    public static bool LaunchExecutable(string handlerId, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !handlerId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(handlerId))
        {
            return false;
        }

        var start = new ProcessStartInfo(handlerId) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(paths[0]) ?? string.Empty };
        foreach (var path in paths)
        {
            start.ArgumentList.Add(path);
        }

        Process.Start(start)?.Dispose();
        return true;
    }

    /// <summary>
    /// The handler's icon as an HICON of about <paramref name="pixelSize"/> pixels, which the
    /// caller destroys with <see cref="ShellIcons.DestroyIcon"/>; zero when it has none that
    /// a file and index describe. A packaged app's icon is an image file instead — see
    /// <see cref="PackagedIconFile"/>.
    /// </summary>
    public static nint Icon(OpenWithHandler handler, int pixelSize)
    {
        var location = handler.IconLocation;
        var index = handler.IconIndex;
        var packaged = location?.StartsWith('@') == true;
        if (packaged && PackagedIconFile(handler) is not null)
        {
            // Its image file is the better icon; the caller loads that.
            return 0;
        }

        if (string.IsNullOrWhiteSpace(location) || packaged)
        {
            if (!File.Exists(handler.Id))
            {
                return 0;
            }

            // A classic app that names no icon: the executable's own first one, as Explorer shows.
            location = handler.Id;
            index = 0;
        }

        var size = (uint)Math.Clamp(pixelSize, 16, 256);
        return SHDefExtractIconW(Environment.ExpandEnvironmentVariables(location), index, 0, out var large, 0, size) == 0 ? large : 0;
    }

    /// <summary>
    /// The image file a packaged app's indirect icon location ("@{Package?ms-resource://…}")
    /// resolves to, or null when the location is not one or doesn't resolve.
    /// </summary>
    public static string? PackagedIconFile(OpenWithHandler handler)
    {
        if (handler.IconLocation is not { } location || !location.StartsWith('@'))
        {
            return null;
        }

        var buffer = new StringBuilder(1024);
        if (SHLoadIndirectString(location, buffer, (uint)buffer.Capacity, 0) != 0)
        {
            return null;
        }

        var path = buffer.ToString();
        return File.Exists(path) ? path : null;
    }

    /// <summary>A shell-style extension: a dot and at least one character.</summary>
    private static bool IsExtension(string extension)
        => extension.Length > 1 && extension[0] == '.' && extension.IndexOfAny(['\\', '/', ':', '\0']) < 0;

    /// <summary>
    /// Walks the handlers, releasing each; <paramref name="visit"/> returns true to stop. A
    /// broken registration (a handler whose app was uninstalled badly) ends the walk quietly.
    /// </summary>
    private static void Enumerate(string extension, bool recommendedOnly, Func<IAssocHandler, bool> visit)
    {
        IEnumAssocHandlers? handlers = null;
        try
        {
            if (SHAssocEnumHandlers(extension, recommendedOnly ? AssocFilterRecommended : AssocFilterNone, out handlers) != 0 || handlers is null)
            {
                return;
            }

            while (handlers.Next(1, out var handler, out var fetched) == 0 && fetched == 1 && handler is not null)
            {
                try
                {
                    if (visit(handler))
                    {
                        return;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(handler);
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
        }
        finally
        {
            if (handlers is not null)
            {
                Marshal.ReleaseComObject(handlers);
            }
        }
    }

    private static OpenWithHandler? Describe(IAssocHandler handler)
    {
        try
        {
            if (handler.GetName(out var id) != 0 || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var name = handler.GetUIName(out var uiName) == 0 && !string.IsNullOrWhiteSpace(uiName)
                ? uiName
                : Path.GetFileNameWithoutExtension(id);
            string? iconLocation = null;
            var iconIndex = 0;
            if (handler.GetIconLocation(out var location, out var index) == 0)
            {
                iconLocation = location;
                iconIndex = index;
            }

            return new OpenWithHandler(id, name, iconLocation, iconIndex);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// An IDataObject for the files, the way Explorer hands a selection to a verb: a shell
    /// item array bound to <c>BHID_DataObject</c>. Null when a path doesn't parse.
    /// </summary>
    private static object? DataObjectFor(IReadOnlyList<string> paths)
    {
        var pidls = new nint[paths.Count];
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                pidls[i] = ILCreateFromPathW(paths[i]);
                if (pidls[i] == 0)
                {
                    return null;
                }
            }

            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array) != 0 || array is null)
            {
                return null;
            }

            try
            {
                var handlerId = BhidDataObject;
                var interfaceId = IidDataObject;
                return array.BindToHandler(0, ref handlerId, ref interfaceId, out var dataObject) == 0 ? dataObject : null;
            }
            finally
            {
                Marshal.ReleaseComObject(array);
            }
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                if (pidl != 0)
                {
                    ILFree(pidl);
                }
            }
        }
    }

    private static string? AssocString(string extension, int kind)
    {
        uint length = 0;
        if (AssocQueryStringW(AssocfNoTruncate, kind, extension, null, null, ref length) != 1 /* S_FALSE: size returned */ || length == 0)
        {
            return null;
        }

        var buffer = new StringBuilder((int)length);
        return AssocQueryStringW(AssocfNoTruncate, kind, extension, null, buffer, ref length) == 0 ? buffer.ToString() : null;
    }

    // MARK: - Interop

    private const int AssocFilterNone = 0;
    private const int AssocFilterRecommended = 1;
    private const int AssocStrExecutable = 2;
    private const int AssocStrFriendlyAppName = 4;
    private const int AssocfNoTruncate = 0x20;

    private static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    private static readonly Guid IidDataObject = new("0000010e-0000-0000-C000-000000000046");

    [ComImport]
    [Guid("973810ae-9599-4b88-9e4d-6ee98c9552da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler? handler, out uint fetched);
    }

    /// <summary>Declared in full, in vtable order, up to the last slot used.</summary>
    [ComImport]
    [Guid("F04061AC-1659-4a3f-A954-775AA57FC083")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig]
        int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        [PreserveSig]
        int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        [PreserveSig]
        int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);

        [PreserveSig]
        int IsRecommended();

        [PreserveSig]
        int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);

        [PreserveSig]
        int Invoke([MarshalAs(UnmanagedType.Interface)] object dataObject);
    }

    /// <summary>Only the first method is used; the rest of the vtable follows it and is never called.</summary>
    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, int filter, [MarshalAs(UnmanagedType.Interface)] out IEnumAssocHandlers? handlers);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, nint[] pidls, [MarshalAs(UnmanagedType.Interface)] out IShellItemArray? array);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(string iconFile, int index, uint flags, out nint large, nint small, uint size);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint capacity, nint reserved);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(int flags, int kind, string association, string? extra, StringBuilder? output, ref uint length);
}
