// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.Cryptography;
using System.Text;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Models;

/// <summary>
/// The rules behind previews of cloud files on gallery tiles (macOS <c>CloudThumbnailLoader</c>).
/// A cloud file has no path for the shell to draw from, so a preview comes from the service
/// (<see cref="CloudProvider.ThumbnailAsync"/>) or, failing that, from a download of a small
/// image or PDF. These are the parts that need no Windows: which files, which errors count as
/// "try again", and how the disk cache is named and trimmed.
/// </summary>
public static class CloudThumbnailRules
{
    /// <summary>The largest file downloaded just to draw its preview.</summary>
    public const long MaxFallbackDownloadBytes = 20L * 1024 * 1024;

    /// <summary>What the disk cache may grow to before the oldest previews go.</summary>
    public const long MaxDiskCacheBytes = 300L * 1024 * 1024;

    /// <summary>Images and PDFs: what the shell can draw from a downloaded copy.</summary>
    private static readonly HashSet<string> DrawableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "webp", "heic", "heif", "avif",
        "ico", "jxr", "wdp", "jxl", "psd", "tga",
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "dng", "orf", "rw2", "raf", "srw", "pef", "erf", "kdc", "mrw", "sr2", "3fr",
        "pdf",
    };

    /// <summary>Videos: previews only come from services that make them.</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "avi", "mkv", "wmv", "webm", "mpg", "mpeg", "3gp", "3g2", "mts", "m2ts", "lrv", "360",
    };

    /// <summary>Whether a tile should ask for a preview at all: files the service or the shell could draw, and nothing synthetic.</summary>
    public static bool WantsThumbnail(CloudFileItem item)
        => !item.IsDirectory && item.Role == CloudItemRole.Normal && item.IconOverride is null &&
           (DrawableExtensions.Contains(item.Extension) || VideoExtensions.Contains(item.Extension));

    /// <summary>
    /// Whether a file may be downloaded to draw its preview when the service has none: a small
    /// image or PDF that is really there. A file iCloud moved off this PC stays where it is —
    /// fetching it for a preview would bring back every photo the user let go.
    /// </summary>
    public static bool MayDownloadForThumbnail(CloudFileItem item)
        => DrawableExtensions.Contains(item.Extension) &&
           item.DownloadStatus != CloudDownloadStatus.Evicted &&
           item.Size is > 0 and <= MaxFallbackDownloadBytes;

    /// <summary>
    /// Failures that say nothing about the file — the network, a cancel — so they aren't
    /// remembered and the tile asks again next time. Any other refusal (as on macOS) leaves the
    /// download to try.
    /// </summary>
    public static bool IsTransient(Exception error) => error switch
    {
        OperationCanceledException => true,
        HttpRequestException { StatusCode: null } => true,
        TimeoutException => true,
        IOException { InnerException: System.Net.Sockets.SocketException } => true,
        CloudProviderException { Kind: CloudErrorKind.NetworkError or CloudErrorKind.Cancelled } => true,
        _ => false,
    };

    /// <summary>
    /// The disk cache's file name for one (account, path, version, size, edge). A hash, so nothing
    /// of a remote path reaches the file system.
    /// </summary>
    public static string CacheFileName(Guid accountId, CloudFileItem item, int pixels)
    {
        var identity = string.Join(
            '\u001F',
            accountId.ToString("D"),
            item.Path,
            item.ModificationDate.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pixels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".thumb";
    }

    /// <summary>
    /// Drops the oldest previews until the folder fits <paramref name="limit"/>, down to three
    /// quarters of it so the next launch doesn't prune again at once.
    /// </summary>
    public static void Prune(string folder, long limit)
    {
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(folder).GetFiles("*.thumb");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var total = files.Sum(f => f.Length);
        if (total <= limit)
        {
            return;
        }

        var target = limit / 4 * 3;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= target)
            {
                break;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // In use or gone already; the next one will do.
            }
        }
    }
}
