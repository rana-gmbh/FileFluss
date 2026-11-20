// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.ICloud;
using FileFluss.Core.Settings;

namespace FileFluss.App.Services;

/// <summary>
/// Previews of cloud files on gallery tiles (macOS <c>CloudThumbnailLoader</c>). A cloud file
/// has no path for the shell, so a preview comes from:
/// <list type="number">
/// <item>the service itself (<see cref="CloudProvider.ThumbnailAsync"/>) — a few kilobytes, and
/// the only way videos get a preview;</item>
/// <item>failing that, a download of a small image or PDF, drawn by the shell.</item>
/// </list>
/// iCloud Drive's files are on this PC already and are drawn in place, unless iCloud moved them
/// off it. Previews are scaled to the tile and kept on disk, so a folder seen once shows them at
/// once the next time, without the network.
///
/// <para>Works like <see cref="ThumbnailCache"/>: tiles ask only while on screen, get the type
/// icon at once and the preview later on the UI thread; requests are served newest first and
/// only the most recent ones are kept, so after a fast scroll the tiles now on screen come first
/// and those scrolled past are dropped (they ask again when they come back). A few fetches run
/// at a time. A refusal is remembered; a network failure is not.</para>
/// </summary>
public static class CloudThumbnailCache
{
    private const int MaxEntries = 2048;
    private const int MaxPending = 160;
    private const long MaxCost = 24_000_000;
    private const int WorkerCount = 4;

    /// <summary>Long enough for a 20 MB download on a slow line; a stuck request must not hold a worker for ever.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, (ImageSource Image, long Cost)> Cache = [];
    private static readonly Queue<Key> Order = new();
    private static readonly HashSet<Key> Failed = [];
    private static readonly Dictionary<Key, List<Action<ImageSource>>> Waiting = [];
    private static readonly LinkedList<Request> Pending = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static long _totalCost;
    private static bool _started;
    private static bool _pruned;

    private static string Folder => Path.Combine(SettingsStore.DataDirectory, "CloudThumbnails");

    private readonly record struct Key(Guid AccountId, string Path, long ModifiedTicks, long Size, int Pixels);

    private sealed record Request(Key Key, CloudProvider Provider, CloudFileItem Item);

    private enum Outcome
    {
        Image,
        Refused,
        Transient,
    }

    /// <summary>
    /// The preview if it is ready; otherwise queues it and calls <paramref name="loaded"/> on
    /// the UI thread once it is. Null at once for files that have no preview.
    /// </summary>
    public static ImageSource? Get(CloudProvider provider, CloudFileItem item, Action<ImageSource> loaded, int pixelSize)
    {
        var key = new Key(provider.AccountId, item.Path, item.ModificationDate.UtcTicks, item.Size, pixelSize);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached.Image;
            }

            if (Failed.Contains(key))
            {
                return null;
            }

            if (Waiting.TryGetValue(key, out var callbacks))
            {
                callbacks.Add(loaded);
                return null;
            }

            Waiting[key] = [loaded];
            Pending.AddFirst(new Request(key, provider, item));
            while (Pending.Count > MaxPending)
            {
                Waiting.Remove(Pending.Last!.Value.Key);
                Pending.RemoveLast();
            }

            if (!_started)
            {
                _started = true;
                for (var i = 0; i < WorkerCount; i++)
                {
                    _ = Task.Run(WorkAsync);
                }
            }
        }

        Signal.Release();
        return null;
    }

    private static async Task WorkAsync()
    {
        while (true)
        {
            await Signal.WaitAsync().ConfigureAwait(false);
            Request request;
            lock (Gate)
            {
                if (Pending.First is not { } first)
                {
                    continue;
                }

                request = first.Value;
                Pending.RemoveFirst();
            }

            (Outcome outcome, ImageSource? image) result;
            try
            {
                using var timeout = new CancellationTokenSource(RequestTimeout);
                result = await LoadAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Whatever went wrong, the worker carries on; the tile keeps its icon.
                result = (CloudThumbnailRules.IsTransient(e) ? Outcome.Transient : Outcome.Refused, null);
            }

            Finish(request.Key, result.outcome, result.image);
        }
    }

    private static void Finish(Key key, Outcome outcome, ImageSource? image)
    {
        List<Action<ImageSource>>? callbacks;
        lock (Gate)
        {
            Waiting.Remove(key, out callbacks);
            if (outcome != Outcome.Image || image is null)
            {
                if (outcome == Outcome.Refused)
                {
                    if (Failed.Count > 20_000)
                    {
                        Failed.Clear();
                    }

                    Failed.Add(key);
                }

                return;
            }

            long cost = image is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap
                ? (long)bitmap.PixelWidth * bitmap.PixelHeight
                : (long)key.Pixels * key.Pixels;
            if (Cache.Remove(key, out var previous))
            {
                _totalCost -= previous.Cost;
            }
            else
            {
                Order.Enqueue(key);
            }

            Cache[key] = (image, cost);
            _totalCost += cost;
            while ((Order.Count > MaxEntries || _totalCost > MaxCost) && Order.Count > 0)
            {
                if (Cache.Remove(Order.Dequeue(), out var evicted))
                {
                    _totalCost -= evicted.Cost;
                }
            }
        }

        ImageSource ready = image;
        if (callbacks is { Count: > 0 } && Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(() =>
            {
                foreach (var callback in callbacks)
                {
                    callback(ready);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // MARK: - Loading

    private static async Task<(Outcome, ImageSource?)> LoadAsync(Request request, CancellationToken cancellationToken)
    {
        var (key, provider, item) = request;

        // iCloud Drive: the file is right here — unless iCloud moved it off this PC, and then
        // it stays in the cloud. The shell draws it in place, videos included.
        if (provider is ICloudProvider iCloud)
        {
            if (item.DownloadStatus == CloudDownloadStatus.Evicted)
            {
                return (Outcome.Refused, null);
            }

            var image = await OnStaAsync(() => IconCache.Thumbnail(iCloud.LocalPath(item.Path), key.Pixels)).ConfigureAwait(false);
            return image is null ? (Outcome.Refused, null) : (Outcome.Image, image);
        }

        var file = Path.Combine(Folder, CloudThumbnailRules.CacheFileName(key.AccountId, item, key.Pixels));
        if (ReadCached(file, key.Pixels) is { } cached)
        {
            return (Outcome.Image, cached);
        }

        try
        {
            if (await provider.ThumbnailAsync(item, key.Pixels, cancellationToken).ConfigureAwait(false) is { Length: > 0 } bytes &&
                Decode(bytes, key.Pixels) is { } image)
            {
                Store(file, image);
                return (Outcome.Image, image);
            }
        }
        catch (Exception e) when (CloudThumbnailRules.IsTransient(e))
        {
            return (Outcome.Transient, null);
        }
        catch (Exception e) when (e is CloudProviderException or HttpRequestException or IOException or InvalidOperationException)
        {
            // A type the service can't preview, a file too big for its previewer: the
            // download below may still manage.
        }

        if (!CloudThumbnailRules.MayDownloadForThumbnail(item))
        {
            return (Outcome.Refused, null);
        }

        return await DrawFromDownloadAsync(provider, item, file, key.Pixels, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(Outcome, ImageSource?)> DrawFromDownloadAsync(CloudProvider provider, CloudFileItem item, string cacheFile, int pixels, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FileFlussThumbnails", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);

            // The copy keeps the extension — the shell picks its thumbnail handler by it — but
            // nothing else of the untrusted remote name.
            var local = Path.Combine(folder, item.Extension.Length > 0 ? "file." + item.Extension : "file");
            try
            {
                await provider.DownloadFileAsync(item.Path, local, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return (CloudThumbnailRules.IsTransient(e) ? Outcome.Transient : Outcome.Refused, null);
            }

            var image = await OnStaAsync(() => IconCache.Thumbnail(local, pixels)).ConfigureAwait(false);
            if (image is not BitmapSource bitmap)
            {
                return (Outcome.Refused, null);
            }

            Store(cacheFile, bitmap);
            return (Outcome.Image, bitmap);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still held by a thumbnail handler; the temp folder is cleaned up by Windows.
            }
        }
    }

    /// <summary>
    /// The shell's thumbnail handlers are COM objects that expect an STA. A thread per preview
    /// is cheap next to the download before it, and at most <see cref="WorkerCount"/> run.
    /// </summary>
    private static Task<ImageSource?> OnStaAsync(Func<ImageSource?> work)
    {
        var done = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        })
        { IsBackground = true, Name = "Cloud thumbnails", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // MARK: - Images

    /// <summary>
    /// The image in <paramref name="bytes"/> decoded at no more than <paramref name="pixels"/> on
    /// its longer edge, frozen for the UI thread. Null if the bytes aren't an image WPF can read
    /// (an HTML error page, a format without a codec).
    /// </summary>
    internal static BitmapSource? Decode(byte[] bytes, int pixels)
    {
        try
        {
            int width, height;
            using (var probe = new MemoryStream(bytes))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            if (width >= height)
            {
                image.DecodePixelWidth = Math.Min(pixels, width);
            }
            else
            {
                image.DecodePixelHeight = Math.Min(pixels, height);
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or COMException or InvalidOperationException or IOException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>JPEG for opaque previews, PNG where there is transparency to keep.</summary>
    internal static byte[]? Encode(BitmapSource image)
    {
        try
        {
            BitmapEncoder encoder = image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Bgra32
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BitmapSource? ReadCached(string file, int pixels)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var image = Decode(File.ReadAllBytes(file), pixels);
            if (image is null)
            {
                // A damaged entry (a crash mid-write): drop it and fetch afresh.
                File.Delete(file);
            }

            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Store(string file, BitmapSource image)
    {
        if (Encode(image) is not { } bytes)
        {
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(folder);

            // Written beside and moved into place, so a reader never sees half a file.
            var partial = file + "." + Guid.NewGuid().ToString("N") + ".part";
            File.WriteAllBytes(partial, bytes);
            File.Move(partial, file, overwrite: true);

            var prune = false;
            lock (Gate)
            {
                if (!_pruned)
                {
                    _pruned = true;
                    prune = true;
                }
            }

            // Once per launch: the cache only grows by a few kilobytes per preview.
            if (prune)
            {
                CloudThumbnailRules.Prune(folder, CloudThumbnailRules.MaxDiskCacheBytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A full disk or a locked file: the preview still shows, it just isn't kept.
        }
    }
}
