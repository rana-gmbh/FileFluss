// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Runtime.InteropServices;
using FileFluss.Core.Providers;

namespace FileFluss.Native;

/// <summary>Recycle Bin, CopyFileEx, and the shell verbs Explorer offers on a file.</summary>
public static class ShellFileOperations
{
    // MARK: - Recycle Bin

    /// <summary>
    /// Moves a file or folder to the Recycle Bin silently. False when it could not — notably
    /// on network shares and removable drives without a Recycle Bin, where Explorer would
    /// offer a permanent delete instead (the caller asks the user, as macOS does for volumes
    /// without a Trash).
    /// </summary>
    public static bool MoveToRecycleBin(string path)
    {
        if (!HasRecycleBin(path))
        {
            return false;
        }

        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofNoConfirmMkdir,
        };
        return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted && !Path.Exists(path);
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
            var copied = CopyFileEx(source, partial, callback, 0, ref cancelFlag, 0);
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

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmMkdir = 0x0200;
    private const ushort FofNoErrorUi = 0x0400;
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
