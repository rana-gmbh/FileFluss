// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Runtime.InteropServices;
using FileFluss.Core.IO;
using FileFluss.Core.Providers;

namespace FileFluss.Native;

/// <summary>Recycle Bin, CopyFileEx, and the shell verbs Explorer offers on a file.</summary>
public static class ShellFileOperations
{
    // MARK: - Recycle Bin

    /// <summary>
    /// Moves a file or folder to the Recycle Bin silently. Null once it is there; otherwise why
    /// it could not be — notably on network shares and removable drives without a Recycle Bin,
    /// where Explorer would offer a permanent delete instead (the caller asks the user, as macOS
    /// does for volumes without a Trash), but also a file open elsewhere, a folder this user
    /// can't change, or a damaged Recycle Bin (macOS 1.6: "a refused Trash names the real cause").
    /// </summary>
    public static RecycleRefusal? MoveToRecycleBin(string path)
    {
        if (!HasRecycleBin(path))
        {
            return new RecycleRefusal(RecycleRefusalKind.NoRecycleBin, 0, SafeRoot(path));
        }

        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0",
            // FOF_WANTNUKEWARNING: without it, FOF_NOCONFIRMATION lets Windows delete for good, without a
            // word, an item too big for the Recycle Bin, or everything when the bin is set to "Don't move
            // files to the Recycle Bin". With it, Windows asks first in exactly those cases.
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofNoConfirmMkdir | FofWantNukeWarning,
        };
        var code = SHFileOperation(ref operation);
        if (code == 0 && !operation.AnyOperationsAborted && !Path.Exists(path))
        {
            return null;
        }

        if (code == 0 && operation.AnyOperationsAborted && Path.Exists(path))
        {
            // The user said no to Windows' "permanently delete?" warning: nothing happened, as asked.
            return new RecycleRefusal(RecycleRefusalKind.Declined);
        }

        return Diagnose(path, code == 0 ? DeOpCancelled : code);
    }

    /// <summary>
    /// Looks closer at a refusal, using only checks that change nothing: whether this user may
    /// delete the item at all (a handle opened for DELETE), and whether the drive's Recycle Bin
    /// folder for this user can be read. Mirrors the macOS check of the Trash folder and the
    /// item's folder.
    /// </summary>
    internal static RecycleRefusal Diagnose(string path, int code)
    {
        var kind = RecycleRefusal.Classify(code);
        var root = SafeRoot(path);
        if (kind == RecycleRefusalKind.NotFound && Path.Exists(path))
        {
            kind = RecycleRefusalKind.Unknown;
        }

        // The Recycle Bin keeps paths no longer than MAX_PATH.
        if (path.Length >= MaxPath && kind is RecycleRefusalKind.Unknown or RecycleRefusalKind.AccessDenied or RecycleRefusalKind.NotFound)
        {
            return new RecycleRefusal(RecycleRefusalKind.PathTooLong, code, root);
        }

        if (kind is RecycleRefusalKind.AccessDenied or RecycleRefusalKind.Unknown or RecycleRefusalKind.InUse)
        {
            switch (ProbeDeleteAccess(path))
            {
                case ErrorSharingViolation or ErrorLockViolation:
                    return new RecycleRefusal(RecycleRefusalKind.InUse, code, Path.GetDirectoryName(path));
                case ErrorAccessDenied:
                    return new RecycleRefusal(RecycleRefusalKind.NoPermission, code, Path.GetDirectoryName(path) ?? root);
                case 0 when RecycleBinFolderUnusable(root):
                    return new RecycleRefusal(RecycleRefusalKind.RecycleBinDamaged, code, root);
            }
        }

        return new RecycleRefusal(kind, code, kind == RecycleRefusalKind.RecycleBinDamaged ? root : Path.GetDirectoryName(path));
    }

    /// <summary>
    /// 0 when this user could delete the item (a DELETE handle opens), else the Win32 error —
    /// access denied for a folder that is shut to them, a sharing violation for a file open in
    /// another program. The handle is closed at once; nothing is deleted.
    /// </summary>
    internal static int ProbeDeleteAccess(string path)
    {
        using var handle = CreateFile(ExtendedPath(path), DeleteAccess, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        return handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    /// <summary>
    /// Whether this user's Recycle Bin folder on the drive (<c>X:\$Recycle.Bin\&lt;SID&gt;</c>)
    /// exists and can't be read — the state Explorer reports as "the Recycle Bin is corrupted".
    /// A missing folder is fine: the shell creates it on first use.
    /// </summary>
    internal static bool RecycleBinFolderUnusable(string root)
    {
        try
        {
            if (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value is not { } sid || root.Length == 0)
            {
                return false;
            }

            var folder = Path.Combine(root, "$Recycle.Bin", sid);
            if (File.Exists(folder))
            {
                return true;
            }

            if (!Directory.Exists(folder))
            {
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            _ = entries.MoveNext();
            return false;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private static string SafeRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary>Fixed local disks have a Recycle Bin; network shares and most removable media don't.</summary>
    public static bool HasRecycleBin(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            // Fixed volumes have a Recycle Bin; MoveToRecycleBin still verifies the file is gone,
            // so a bin switched off by policy falls back to the permanent-delete question.
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            return false;
        }
    }

    // MARK: - CopyFileEx

    /// <summary>
    /// Copies one file with CopyFileEx: attributes, alternate data streams and timestamps
    /// carry over, SMB servers can copy server-side, and progress arrives per chunk. Staged
    /// through a partial file so a cancelled copy never leaves a truncated original-looking file.
    /// </summary>
    public static Task CopyFileAsync(string source, string destination, ByteProgress? onBytes, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var partial = destination + ".fileflusspart";
            long last = 0;
            var cancel = 0;
            Exception? callbackFailure = null;
            using var registration = cancellationToken.Register(() => Volatile.Write(ref cancel, 1));
            CopyProgressRoutine callback = (total, transferred, _, _, _, _, _, _, _) =>
            {
                // Called from inside CopyFileEx: an exception escaping here would cross native
                // frames, which ends the process on the spot. Stop the copy and rethrow after.
                try
                {
                    var delta = transferred - last;
                    last = transferred;
                    if (delta > 0)
                    {
                        onBytes?.Invoke(delta);
                    }
                }
                catch (Exception e)
                {
                    callbackFailure = e;
                    return ProgressCancel;
                }

                return Volatile.Read(ref cancel) == 1 ? ProgressCancel : ProgressContinue;
            };

            var cancelFlag = 0;
            var copied = CopyFileEx(ExtendedPath(source), ExtendedPath(partial), callback, 0, ref cancelFlag, 0);
            var error = Marshal.GetLastPInvokeError();
            GC.KeepAlive(callback);
            if (!copied)
            {
                TryDelete(partial);
                if (callbackFailure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackFailure).Throw();
                }

                cancellationToken.ThrowIfCancellationRequested();
                throw CloudProviderException.CommandFailed(new System.ComponentModel.Win32Exception(error).Message);
            }

            File.Move(partial, destination, overwrite: true);
        }, cancellationToken);

    /// <summary>
    /// The "\\?\" form of a long path. .NET's own file APIs add it themselves, but a path handed
    /// to Win32 directly is cut off at MAX_PATH (260) unless the process is long-path aware and
    /// the machine has LongPathsEnabled — so a copy deep in a synced tree failed with "path not
    /// found" while every other step worked.
    /// </summary>
    internal static string ExtendedPath(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // MARK: - Shell verbs

    /// <summary>Opens with the default app, like a double-click in Explorer.</summary>
    public static void Open(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty })?.Dispose();

    /// <summary>The system "Open with" chooser.</summary>
    public static void OpenWith(string path, nint owner)
    {
        var info = new OpenAsInfo { File = path, Flags = OaifExec | OaifAllowRegistration };
        _ = SHOpenWithDialog(owner, ref info);
    }

    /// <summary>The Explorer Properties sheet.</summary>
    public static void ShowProperties(string path, nint owner)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Verb = "properties",
            File = path,
            Show = 1,
            Mask = SeeMaskInvokeIdList,
            Window = owner,
        };
        ShellExecuteEx(ref info);
    }

    /// <summary>Explorer with the items selected ("Show in Explorer").</summary>
    public static void RevealInExplorer(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var folder = Path.GetDirectoryName(paths[0]);
        if (folder is null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{paths[0]}\"") { UseShellExecute = true })?.Dispose();
            return;
        }

        var parent = ILCreateFromPathW(folder);
        var children = paths.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase))
            .Select(ILCreateFromPathW).Where(p => p != 0).ToArray();
        try
        {
            if (parent == 0 || SHOpenFolderAndSelectItems(parent, (uint)children.Length, children, 0) != 0)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{paths[0]}\"") { UseShellExecute = true })?.Dispose();
            }
        }
        finally
        {
            foreach (var child in children)
            {
                ILFree(child);
            }

            if (parent != 0)
            {
                ILFree(parent);
            }
        }
    }

    // MARK: - Interop

    private const int MaxPath = 260;
    private const int DeOpCancelled = 0x75;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmMkdir = 0x0200;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;
    private const uint ProgressContinue = 0;
    private const uint ProgressCancel = 1;
    private const int OaifAllowRegistration = 0x1;
    private const int OaifExec = 0x4;
    private const uint SeeMaskInvokeIdList = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public nint InstApp;
        public nint IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint KeyClass;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    private delegate uint CopyProgressRoutine(long total, long transferred, long streamSize, long streamTransferred, uint streamNumber, uint reason, nint source, nint destination, nint data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFileEx(string existing, string target, CopyProgressRoutine progress, nint data, ref int cancel, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(nint owner, ref OpenAsInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint[] items, uint flags);
}
