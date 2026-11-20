// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using System.Text;
using FileFluss.Core.Search;
using Microsoft.Win32.SafeHandles;

namespace FileFluss.Native;

/// <summary>
/// The mounted volumes as the drive registry wants them: serial number (a drive's identity
/// whatever letter it comes back under), label, root, type, and whether a "fixed" disk sits on
/// an external bus (USB enclosures and many USB SSDs report <see cref="DriveType.Fixed"/>).
/// </summary>
public static class VolumeIdentity
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    // STORAGE_BUS_TYPE values that leave with their hardware.
    private const byte BusType1394 = 0x4;
    private const byte BusTypeUsb = 0x7;
    private const byte BusTypeSd = 0xC;
    private const byte BusTypeMmc = 0xD;

    public static IReadOnlyList<DriveIdentity> Mounted()
    {
        var result = new List<DriveIdentity>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.RootDirectory.FullName;
            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }

            if (!ready || Serial(root) is not { } serial)
            {
                continue;
            }

            string label;
            try
            {
                label = drive.VolumeLabel;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                label = string.Empty;
            }

            result.Add(new DriveIdentity(serial, label, root, drive.DriveType)
            {
                IsExternalBus = drive.DriveType == DriveType.Fixed && IsExternalBus(root),
            });
        }

        return result;
    }

    /// <summary>"1A2B-3C4D", as <c>vol</c> prints it; null when Windows can't read it.</summary>
    public static string? Serial(string root)
    {
        var name = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        if (!GetVolumeInformation(root, name, name.Capacity, out var serial, out _, out _, fileSystem, fileSystem.Capacity))
        {
            return null;
        }

        return $"{serial >> 16:X4}-{serial & 0xFFFF:X4}";
    }

    private static bool IsExternalBus(string root)
    {
        var letter = root.TrimEnd('\\');
        using var handle = CreateFile($@"\\.\{letter}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return false;
        }

        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0) }.
        var query = new byte[12];
        var buffer = new byte[1024];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out var returned, 0) || returned < 29)
        {
            return false;
        }

        // STORAGE_DEVICE_DESCRIPTOR.BusType sits at offset 28.
        return buffer[28] is BusTypeUsb or BusType1394 or BusTypeSd or BusTypeMmc;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetVolumeInformation(
        string rootPathName, StringBuilder volumeName, int volumeNameSize, out uint serialNumber,
        out uint maxComponentLength, out uint fileSystemFlags, StringBuilder fileSystemName, int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
}
