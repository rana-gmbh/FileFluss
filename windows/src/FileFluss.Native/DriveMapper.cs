// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FileFluss.Core.Diagnostics;
using Microsoft.Win32;
using static FileFluss.Core.Localization;

namespace FileFluss.Native;

/// <summary>The state of the Windows WebClient service (the WebDAV redirector's user-mode half).</summary>
public enum WebClientState
{
    /// <summary>Not present: Windows Server without the "WebDAV Redirector" feature, or a stripped image.</summary>
    NotInstalled,

    /// <summary>Start type "Disabled" — only an administrator can change that.</summary>
    Disabled,

    /// <summary>Installed and allowed to start; Windows starts it on the first WebDAV connection.</summary>
    Stopped,

    Running,

    /// <summary>The service manager couldn't be asked (unusual; mapping is attempted anyway).</summary>
    Unknown,
}

public enum DriveMappingError
{
    WebClientMissing,
    WebClientDisabled,
    NoFreeDriveLetter,
    Failed,
}

/// <summary>A drive mapping that failed, with a message fit to show the user.</summary>
public sealed class DriveMappingException(DriveMappingError kind, string message, int win32Error = 0)
    : Exception(message)
{
    public DriveMappingError Kind { get; } = kind;

    public int Win32Error { get; } = win32Error;
}

/// <summary>One drive letter that points at a FileFluss loopback mount.</summary>
public sealed record LoopbackDrive(string DriveLetter, string RemoteName, int Port, string Secret);

/// <summary>
/// Maps a loopback <see cref="FileFluss.Core.Mount.WebDavServer"/> as a drive letter through
/// Windows' WebDAV redirector — the counterpart of macOS' <c>NetFSMountURLSync</c> call in
/// <c>LoopbackMountService</c>.
///
/// <para><b>How.</b> <c>WNetAddConnection2</c> with the UNC form of the server URL,
/// <c>\\127.0.0.1@port\FileFluss-…</c>; the DAV network provider hands it to the WebClient
/// service. Not persistent (no <c>CONNECT_UPDATE_PROFILE</c>): the drive must never come
/// back at the next sign-in pointing at a server that no longer runs. Explorer labels a
/// network drive "share (\\server)", which here would show the random secret, so the
/// friendly name goes into the per-user <c>MountPoints2</c> key's <c>_LabelFromReg</c> value
/// — the only registry value this class writes, and it is removed again on unmap.</para>
///
/// <para><b>WebClient.</b> The redirector depends on the WebClient service. On Windows 10/11
/// it is installed with start type Manual and Windows starts it by trigger on the first
/// WebDAV connection, so a stopped service is fine. FileFluss never starts, configures or
/// re-enables it itself; when it is missing or disabled the user gets an explanation.
/// (Microsoft has announced WebClient as deprecated; it still ships enabled-by-trigger in
/// current Windows 11 builds.)</para>
///
/// <para><b>Limits Windows imposes</b> (registry values under
/// <c>HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters</c>, admin-only, read here
/// for messages but never changed):
/// <list type="bullet">
/// <item><c>FileSizeLimitInBytes</c>, default 50,000,000: the redirector refuses to read or
/// write any file larger than that ("The file size exceeds the limit allowed").</item>
/// <item><c>FileAttributesLimitInBytes</c>, default 1,000,000: a folder whose PROPFIND
/// answer is bigger shows as empty — a few thousand entries.</item>
/// <item><c>BasicAuthLevel</c>, default 1: Basic credentials only over HTTPS. The mount uses
/// an unguessable path instead of a password for exactly that reason.</item>
/// <item><c>SendReceiveTimeoutInSec</c>, default 60: a download or upload that takes the
/// server longer than that to finish against the cloud fails in Explorer.</item>
/// </list></para>
///
/// <para>Drive mappings belong to a logon session. An elevated FileFluss maps into the
/// elevated session, which Explorer (not elevated) doesn't see unless
/// <c>EnableLinkedConnections</c> is set — FileFluss is meant to run unelevated.</para>
/// </summary>
public static partial class DriveMapper
{
    public const string WebClientParametersKey = @"SYSTEM\CurrentControlSet\Services\WebClient\Parameters";

    /// <summary>Explorer's documented default for <c>FileSizeLimitInBytes</c>.</summary>
    public const long DefaultFileSizeLimitBytes = 50_000_000;

    private const string MountPointsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2";

    // MARK: - WebClient

    /// <summary>Asks the service manager about WebClient (read-only rights).</summary>
    public static WebClientState QueryWebClient()
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return WebClientState.Unknown;
        }

        try
        {
            var service = OpenServiceW(manager, "WebClient", ServiceQueryStatus | ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? WebClientState.NotInstalled : WebClientState.Unknown;
            }

            try
            {
                if (StartType(service) == ServiceDisabled)
                {
                    return WebClientState.Disabled;
                }

                return QueryServiceStatus(service, out var status)
                    ? status.CurrentState == ServiceRunning ? WebClientState.Running : WebClientState.Stopped
                    : WebClientState.Unknown;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>The redirector's per-file size cap, or null when it can't be read.</summary>
    public static long? FileSizeLimitBytes() => ReadParameter("FileSizeLimitInBytes", DefaultFileSizeLimitBytes);

    /// <summary>The redirector's cap on one PROPFIND answer (folder listing), or null when it can't be read.</summary>
    public static long? FileAttributesLimitBytes() => ReadParameter("FileAttributesLimitInBytes", 1_000_000);

    /// <summary>
    /// The user-facing explanation of the file size limit, for the mount confirmation.
    /// </summary>
    public static string FileSizeLimitNotice(long limitBytes)
        => L("Windows only opens and saves files up to {0} on drives like this one. Larger files can still be copied with FileFluss itself. An administrator can raise the limit (FileSizeLimitInBytes of the WebClient service).",
            Core.Formatting.ByteFormatter.Format(limitBytes));

    private static long? ReadParameter(string name, long fallback)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WebClientParametersKey);
            return key?.GetValue(name) switch
            {
                int dword => unchecked((uint)dword),
                long qword => qword,
                null => fallback,
                _ => null,
            };
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    // MARK: - Mapping

    /// <summary>
    /// Maps <paramref name="uncPath"/> (<c>\\127.0.0.1@port\share</c>) to a free drive letter,
    /// labelled <paramref name="label"/> in Explorer, and returns "X:". Letters are tried from
    /// Z: down, so the mount stays out of the way of USB sticks and card readers that take the
    /// first free letter. Throws <see cref="DriveMappingException"/> with a message for the user.
    /// </summary>
    public static async Task<string> MapAsync(string uncPath, string label, CancellationToken cancellationToken = default)
    {
        switch (QueryWebClient())
        {
            case WebClientState.NotInstalled:
                throw new DriveMappingException(
                    DriveMappingError.WebClientMissing,
                    L("Showing a cloud account as a drive needs the Windows \"WebClient\" service, which isn't installed on this PC. On Windows Server, add the \"WebDAV Redirector\" feature."));
            case WebClientState.Disabled:
                throw new DriveMappingException(
                    DriveMappingError.WebClientDisabled,
                    L("The Windows \"WebClient\" service is disabled, so the account can't appear as a drive. An administrator can set it to \"Manual\" in Services (services.msc)."));
        }

        SetLabel(uncPath, label);
        try
        {
            // WNetAddConnection2 blocks — for many seconds when the redirector has to start
            // WebClient first — so it runs off the caller's thread. It can't be interrupted;
            // cancellation abandons the wait and the result is cleaned up below.
            var task = Task.Run(() => MapToFreeLetter(uncPath), CancellationToken.None);
            try
            {
                var drive = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                NotifyShell(ShcneDriveAdd, drive + "\\");
                SupportLogger.Shared.Log($"Mapped {drive} to a FileFluss loopback mount", Core.Mount.WebDavServer.LogCategory);
                return drive;
            }
            catch (OperationCanceledException)
            {
                _ = task.ContinueWith(t => Unmap(t.Result), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                throw;
            }
        }
        catch
        {
            RemoveLabel(uncPath);
            throw;
        }
    }

    /// <summary>Removes the mapping (forcefully: open files on a dead server must not block it) and its label.</summary>
    public static Task UnmapAsync(string driveLetter, string? uncPath = null)
        => Task.Run(() =>
        {
            Unmap(driveLetter);
            if (uncPath is not null)
            {
                RemoveLabel(uncPath);
            }
        });

    private static string MapToFreeLetter(string uncPath)
    {
        var lastError = 0;
        foreach (var letter in CandidateLetters())
        {
            var local = letter + ":";
            var resource = new NetResource
            {
                Type = ResourceTypeDisk,
                LocalName = local,
                RemoteName = uncPath,
            };
            var result = WNetAddConnection2W(ref resource, null, null, 0);
            if (result == 0)
            {
                return local;
            }

            lastError = result;
            if (result is ErrorAlreadyAssigned or ErrorDeviceAlreadyRemembered)
            {
                // Taken between the scan and the call — try the next one.
                continue;
            }

            throw MappingFailure(result);
        }

        throw lastError == 0
            ? new DriveMappingException(DriveMappingError.NoFreeDriveLetter, L("No free drive letter is available. Disconnect a network drive and try again."))
            : MappingFailure(lastError);
    }

    private static void Unmap(string driveLetter)
    {
        var result = WNetCancelConnection2W(driveLetter, 0, true);
        if (result is 0 or ErrorNotConnected)
        {
            NotifyShell(ShcneDriveRemoved, driveLetter + "\\");
            return;
        }

        SupportLogger.Shared.Log($"Unmapping {driveLetter} failed: {new Win32Exception(result).Message}", Core.Mount.WebDavServer.LogCategory, LogLevel.Error);
    }

    /// <summary>Z: … D: that are neither in use nor remembered for a reconnect.</summary>
    internal static IEnumerable<char> CandidateLetters()
    {
        var used = GetLogicalDrives();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if ((used & (1u << (letter - 'A'))) != 0)
            {
                continue;
            }

            // A remembered but disconnected network drive doesn't show in GetLogicalDrives,
            // yet mapping over it fails (or worse, silently replaces the user's mapping).
            if (RemoteNameOf(letter + ":") is not null)
            {
                continue;
            }

            yield return letter;
        }
    }

    private static DriveMappingException MappingFailure(int error)
    {
        var reason = error switch
        {
            // The redirector couldn't reach the server, or no network provider took the
            // path at all — both what a WebClient that failed to start looks like.
            ErrorBadNetName or ErrorBadNetPath or ErrorNoNetOrBadPath or ErrorNoNetwork
                => L("Windows' WebDAV redirector (the \"WebClient\" service) couldn't connect to FileFluss. Make sure the WebClient service isn't blocked by a policy or security software."),
            _ => new Win32Exception(error).Message,
        };
        SupportLogger.Shared.Log($"WNetAddConnection2 failed: {error} ({new Win32Exception(error).Message})", Core.Mount.WebDavServer.LogCategory, LogLevel.Error);
        return new DriveMappingException(DriveMappingError.Failed, L("Could not mount in File Explorer: {0}", reason), error);
    }

    // MARK: - Leftovers

    /// <summary>
    /// Every drive letter that points at a FileFluss loopback mount (its share name is a
    /// mount secret on 127.0.0.1/localhost). The redirector reports the UNC form, sometimes
    /// with <c>DavWWWRoot</c> or a trailing backslash; the URL form is accepted as well.
    /// </summary>
    public static IReadOnlyList<LoopbackDrive> LoopbackDrives()
    {
        var drives = new List<LoopbackDrive>();
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var local = letter + ":";
            if (RemoteNameOf(local) is { } remote && ParseLoopbackRemote(remote) is { } parsed)
            {
                drives.Add(new LoopbackDrive(local, remote, parsed.Port, parsed.Secret));
            }
        }

        return drives;
    }

    /// <summary>
    /// The macOS <c>cleanupOrphanMounts</c>: at launch, drives left by a run that crashed
    /// or was killed point at a server that no longer exists and make Explorer hang on
    /// them. Unmaps every FileFluss loopback drive and removes stale labels. Call it before
    /// the first mount of a session. Returns the number of drives removed.
    /// </summary>
    public static int CleanupOrphans()
    {
        var removed = 0;
        foreach (var drive in LoopbackDrives())
        {
            Unmap(drive.DriveLetter);
            removed++;
            SupportLogger.Shared.Log($"Removed leftover FileFluss drive {drive.DriveLetter}", Core.Mount.WebDavServer.LogCategory);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MountPointsKey, writable: true);
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                if (OurLabelKey().IsMatch(name))
                {
                    key!.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return removed;
    }

    internal static (int Port, string Secret)? ParseLoopbackRemote(string remote)
    {
        var match = LoopbackRemote().Match(remote);
        if (!match.Success)
        {
            return null;
        }

        var port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);
        var secret = match.Groups["secret"].Value;
        return Core.Mount.WebDavServer.IsMountSecret(secret) ? (port, secret) : null;
    }

    [GeneratedRegex(@"^(?:\\\\(?:127\.0\.0\.1|localhost)@(?<port>\d{1,5})\\(?:DavWWWRoot\\)?|https?://(?:127\.0\.0\.1|localhost):(?<port>\d{1,5})/)(?<secret>FileFluss-[0-9A-Fa-f]{32})(?:[\\/].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoopbackRemote();

    [GeneratedRegex(@"^##(?:127\.0\.0\.1|localhost)@\d{1,5}#FileFluss-[0-9A-Fa-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OurLabelKey();

    // MARK: - Labels

    /// <summary>
    /// <c>\\127.0.0.1@54321\FileFluss-ab…</c> → <c>##127.0.0.1@54321#FileFluss-ab…</c>:
    /// Explorer's per-user key for a network location, backslashes replaced by '#'.
    /// </summary>
    internal static string LabelKeyName(string uncPath) => uncPath.TrimEnd('\\').Replace('\\', '#');

    /// <summary>Explorer shows <c>_LabelFromReg</c> instead of "share (\\server)" for the drive.</summary>
    public static void SetLabel(string uncPath, string label)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(MountPointsKey + "\\" + LabelKeyName(uncPath), writable: true);
            key.SetValue("_LabelFromReg", label, RegistryValueKind.String);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Cosmetic: the drive works with Explorer's default label too.
        }
    }

    public static void RemoveLabel(string uncPath)
    {
        var name = LabelKeyName(uncPath);
        if (!OurLabelKey().IsMatch(name))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MountPointsKey, writable: true);
            key?.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    // MARK: - Win32

    /// <summary>The remote name a local device is connected (or remembered) to; null when none.</summary>
    private static string? RemoteNameOf(string localName)
    {
        var length = 1024;
        var buffer = new StringBuilder(length);
        var result = WNetGetConnectionW(localName, buffer, ref length);
        if (result == ErrorMoreData)
        {
            buffer = new StringBuilder(length);
            result = WNetGetConnectionW(localName, buffer, ref length);
        }

        return result is 0 or ErrorConnectionUnavail ? buffer.ToString() : null;
    }

    private static int StartType(IntPtr service)
    {
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            return -1;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            // QUERY_SERVICE_CONFIG starts with dwServiceType, dwStartType.
            return QueryServiceConfigW(service, buffer, needed, out _) ? Marshal.ReadInt32(buffer, 4) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void NotifyShell(int eventId, string path)
    {
        var pointer = Marshal.StringToHGlobalUni(path);
        try
        {
            SHChangeNotify(eventId, ShcnfPathW, pointer, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const int ResourceTypeDisk = 1;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorBadNetPath = 53;
    private const int ErrorBadNetName = 67;
    private const int ErrorMoreData = 234;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorConnectionUnavail = 1201;
    private const int ErrorDeviceAlreadyRemembered = 1202;
    private const int ErrorNoNetOrBadPath = 1203;
    private const int ErrorNoNetwork = 1222;
    private const int ErrorNotConnected = 2250;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ServiceDisabled = 4;
    private const int ServiceRunning = 4;
    private const int ShcneDriveRemoved = 0x00000080;
    private const int ShcneDriveAdd = 0x00000100;
    private const uint ShcnfPathW = 0x0005;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(ref NetResource netResource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);

    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(IntPtr service, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
