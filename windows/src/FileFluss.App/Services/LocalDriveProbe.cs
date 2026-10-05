// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.Core.Providers;

namespace FileFluss.App.Services;

/// <summary>A ready drive as the sidebar shows it: root, Explorer's name, free space.</summary>
public sealed record LocalDriveInfo(string Root, string Label, DriveType Type, long? Free, long? Total);

/// <summary>
/// Which drives are ready, with their names and free space — asked on the thread pool, one
/// drive per task. <see cref="DriveInfo.IsReady"/> and the volume label wait for the drive:
/// a sleeping USB disk or an unreachable network share can take seconds, which on the UI thread
/// froze startup and every device change. Both sidebars asking at once share one probe.
/// </summary>
public static class LocalDriveProbe
{
    private static readonly Lock Gate = new();
    private static Task<IReadOnlyList<LocalDriveInfo>>? _inFlight;

    /// <summary>The drives the last finished probe found; null before the first one finished.</summary>
    public static IReadOnlyList<LocalDriveInfo>? Latest { get; private set; }

    public static Task<IReadOnlyList<LocalDriveInfo>> ProbeAsync()
    {
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false } running)
            {
                return running;
            }

            return _inFlight = Task.Run(ProbeAllAsync);
        }
    }

    private static async Task<IReadOnlyList<LocalDriveInfo>> ProbeAllAsync()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // One slow drive must not hold up the others' answers longer than itself.
        var probes = await Task.WhenAll(drives.Select(drive => Task.Run(() => Probe(drive))));
        var ready = probes.OfType<LocalDriveInfo>().ToList();
        Latest = ready;
        return ready;
    }

    private static LocalDriveInfo? Probe(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady)
            {
                return null;
            }

            var root = drive.RootDirectory.FullName;
            long? free = null, total = null;
            try
            {
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
            catch (IOException)
            {
            }

            return new LocalDriveInfo(root, LocalFileProvider.DriveLabel(root), drive.DriveType, free, total);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
