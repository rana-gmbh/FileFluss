// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;

namespace FileFluss.Core.IO;

/// <summary>What to do with the copy of a cloud file already in the open cache (macOS <c>CachedCopyDecision</c>).</summary>
public enum CachedCopyDecision
{
    /// <summary>The copy is ours and still current: open it as it is.</summary>
    Reuse,

    /// <summary>The copy is ours and out of date: replace it.</summary>
    Refresh,

    /// <summary>The copy has been edited since FileFluss wrote it. Keep it — somewhere the user can find it — before downloading again.</summary>
    PreserveEditsThenRefresh,
}

/// <summary>
/// The open cache's rule for copies the user may have edited — macOS 851b79c.
///
/// <para>FileFluss opens a cloud file by downloading it and handing the copy to an app. The
/// copy is stamped: its "Date modified" is the server's modification date (or, when the
/// server reports none, the moment it was downloaded, which is also its "Date created"). A
/// copy some app has saved over carries a later date than that stamp, and that difference
/// is all it takes to tell "our download" from "the user's work".</para>
///
/// <para>It matters because the cache used to replace any copy that no longer matched the
/// server, and cache maintenance deleted copies by age and size: for a file the user had
/// edited, either was FileFluss deleting their only copy of the changes.</para>
/// </summary>
public static class CachedCopy
{
    /// <summary>
    /// Clocks and file systems disagree about sub-second timestamps, and FAT32/exFAT (a cache
    /// folder on a USB stick) keep modification times in two-second steps.
    /// </summary>
    public static readonly TimeSpan DateTolerance = TimeSpan.FromSeconds(2);

    /// <param name="localSize">The cached copy's size.</param>
    /// <param name="localModifiedUtc">The cached copy's "Date modified"; null when unreadable.</param>
    /// <param name="remoteSize">The server's size; 0 when the provider reports none.</param>
    /// <param name="stampUtc">What FileFluss stamped the copy with — see <see cref="CachedCopy"/>.</param>
    public static CachedCopyDecision Decide(long localSize, DateTime? localModifiedUtc, long remoteSize, DateTime stampUtc)
    {
        if (localModifiedUtc is not { } local)
        {
            // No date to compare: treat it as ours and out of date, as the cache always did.
            return CachedCopyDecision.Refresh;
        }

        // Later than the stamp means something wrote to it after the download — and the only
        // thing that does is the app the user opened it with.
        if (local - stampUtc > DateTolerance)
        {
            return CachedCopyDecision.PreserveEditsThenRefresh;
        }

        var sameSize = remoteSize == 0 || localSize == remoteSize;
        var sameDate = (local - stampUtc).Duration() <= DateTolerance;
        return sameSize && sameDate ? CachedCopyDecision.Reuse : CachedCopyDecision.Refresh;
    }

    /// <summary>
    /// For cache maintenance, which has no server date to compare with: a copy saved over
    /// after it was cached. A download is created when it lands and stamped with a date no
    /// later than that, so a "Date modified" past the "Date created" is somebody's edit.
    /// </summary>
    public static bool HoldsEdits(DateTime createdUtc, DateTime modifiedUtc) => modifiedUtc - createdUtc > DateTolerance;

    /// <summary>
    /// The name an edited copy is kept under, beside where it was: "Report (edited 2026-11-09
    /// 14.30.05).docx", with the time it was last written — so several kept versions sort in
    /// order and none overwrites another.
    /// </summary>
    public static string PreservedName(string fileName, DateTime editedLocal)
    {
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        return $"{stem} (edited {editedLocal.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture)}){extension}";
    }

    /// <summary>
    /// Moves an edited copy aside, beside where it was, under <see cref="PreservedName"/> (with
    /// a counter if that is taken too), keeping its dates — so maintenance still recognises
    /// it as the user's. Returns where it went.
    /// </summary>
    public static string Preserve(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? throw new ArgumentException("No folder", nameof(path));
        var name = PreservedName(Path.GetFileName(path), File.GetLastWriteTime(path));
        var target = Path.Combine(folder, name);
        for (var counter = 2; File.Exists(target) || Directory.Exists(target); counter++)
        {
            var extension = Path.GetExtension(name);
            target = Path.Combine(folder, $"{name[..^extension.Length]} {counter}{extension}");
        }

        File.Move(path, target);
        return target;
    }

    /// <summary>
    /// Stamps a fresh download: created now, modified at the server's date (or now when the
    /// server reports none). Setting the creation time matters — a download renamed over an
    /// older copy inherits that copy's creation time (NTFS file-name tunnelling), which would
    /// make a re-download look like an edit to <see cref="HoldsEdits"/>.
    /// </summary>
    public static void Stamp(string path, DateTime? remoteModifiedUtc)
    {
        var now = DateTime.UtcNow;
        File.SetCreationTimeUtc(path, now);
        File.SetLastWriteTimeUtc(path, remoteModifiedUtc ?? now);
    }

    /// <summary>What <see cref="Stamp"/> wrote as "Date modified": the server's date, or else the download time.</summary>
    public static DateTime StampOf(DateTime? remoteModifiedUtc, DateTime createdUtc) => remoteModifiedUtc ?? createdUtc;
}
