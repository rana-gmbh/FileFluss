// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;

namespace FileFluss.Core.IO;

/// <summary>
/// The "mark of the web": a <c>Zone.Identifier</c> stream saying a file came from the
/// Internet zone, as browsers and mail clients attach to what they download. SmartScreen,
/// Office's Protected View and the "This file came from another computer" prompt all key off
/// it. A cloud file FileFluss downloads and hands to its default app came from someone else's
/// server just the same, so it gets the mark before it is opened — otherwise a shared
/// "invoice.pdf.exe" would start without any of those checks.
/// </summary>
public static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";

    /// <summary>ZoneId 3 is URLZONE_INTERNET.</summary>
    private const string Content = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>
    /// Marks <paramref name="path"/> as downloaded from the Internet. Best effort: a volume
    /// without alternate data streams (FAT32, exFAT, some network shares) can't carry the
    /// mark, and the file opens without it, as it would from a browser saving there.
    /// </summary>
    /// <returns>Whether the mark was written.</returns>
    public static bool Apply(string path)
    {
        // Writing the stream of a missing file would create an empty one.
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            if (Zone(path) is >= 3)
            {
                return true;
            }

            // Writing a stream bumps the file's own "Date modified", which the open cache
            // compares against the cloud's to decide whether a copy is still fresh.
            var modified = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path + StreamSuffix, Content);
            File.SetLastWriteTimeUtc(path, modified);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            SupportLogger.Shared.Log($"Couldn't mark a downloaded file as coming from the Internet: {e.Message}", "Open", LogLevel.Notice);
            return false;
        }
    }

    /// <summary>The zone the file is marked with, or null when it carries no mark.</summary>
    public static int? Zone(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path + StreamSuffix))
            {
                if (line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase) && int.TryParse(line.AsSpan(7), out var zone))
                {
                    return zone;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }

        return null;
    }
}
