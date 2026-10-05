// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.Core.Models;
using FileFluss.Native;

namespace FileFluss.App.Services;

/// <summary>
/// File and folder icons as WPF images, straight from Explorer's system image lists, so a
/// file looks the same here as in File Explorer. Cloud files have no path, but an extension
/// is enough for the shell. Icons are cached per system image index; there are only a few
/// dozen distinct ones in practice.
/// </summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<(int Index, ShellIconSize Size), ImageSource?> ByIndex = new();
    private static readonly ConcurrentDictionary<string, ImageSource?> Logos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The 32-px list renders 16-DIP rows crisply up to 200% scaling; larger rows use 48 px.</summary>
    public static ShellIconSize SizeFor(double dips) => dips > 24 ? ShellIconSize.ExtraLarge : ShellIconSize.Large;

    /// <summary>
    /// The item's icon, synchronously. Local folders, programs and shortcuts make the shell read
    /// the item itself (about a millisecond on an SSD, seconds on a sleeping network share):
    /// lists use <see cref="ForRow"/> instead, which never waits for that.
    /// </summary>
    public static ImageSource? For(CloudFileItem item, bool isLocal, double dips = 16)
    {
        var exists = NeedsPathLookup(item, isLocal);
        var index = ShellIcons.IconIndex(exists ? item.Path : item.Name, item.IsDirectory, exists);
        return FromIndex(index, SizeFor(dips));
    }

    public static ImageSource? ForPath(string path, bool isDirectory, double dips = 16)
        => FromIndex(ShellIcons.IconIndex(path, isDirectory, exists: true), SizeFor(dips));

    /// <summary>
    /// The icon for a list row without blocking: an icon that depends on the item itself (a
    /// local folder's desktop.ini, a program, a shortcut, a drive) is looked up on a background
    /// thread. Until then the generic type icon is returned (<c>IsFinal</c> false) and
    /// <paramref name="loaded"/> is called on the UI thread once the real one is known. A row
    /// that is asked again while not final asks again — a lookup dropped because the user
    /// scrolled far past it is then requested anew.
    /// </summary>
    public static (ImageSource? Icon, bool IsFinal) ForRow(CloudFileItem item, bool isLocal, bool isDrive, double dips, Action<ImageSource> loaded)
    {
        var size = SizeFor(dips);
        ImageSource? Generic() => isDrive ? null : FromIndex(ShellIcons.IconIndex(item.Name, item.IsDirectory, exists: false), size);
        if (!isDrive && !NeedsPathLookup(item, isLocal))
        {
            return (Generic(), true);
        }

        var key = new PathKey(item.Path, item.IsDirectory, isDrive ? 0 : item.ModificationDate.UtcTicks, size);
        if (PathLookups.TryGet(key, out var known))
        {
            return (known ?? Generic(), true);
        }

        PathLookups.Request(key, loaded);
        return (Generic(), false);
    }

    private static bool NeedsPathLookup(CloudFileItem item, bool isLocal)
        => isLocal && (item.IsDirectory || ShellIcons.NeedsPerFileIcon(item.Name)) && item.Path.Length > 0;

    /// <summary>A per-item icon request: the path, and its modification time so an edited shortcut or desktop.ini is asked again.</summary>
    private readonly record struct PathKey(string Path, bool IsDirectory, long Stamp, ShellIconSize Size);

    /// <summary>
    /// Per-item icon lookups on one STA thread (SHGetFileInfo wants COM), newest request first
    /// and only the most recent ones kept — after a fast scroll the rows on screen come first.
    /// Results are remembered, so coming back to a folder is instant.
    /// </summary>
    private static class PathLookups
    {
        private const int MaxPending = 256;
        private const int MaxRemembered = 20_000;

        private static readonly object Gate = new();
        private static readonly Dictionary<PathKey, ImageSource?> Known = [];
        private static readonly Dictionary<PathKey, List<Action<ImageSource>>> Waiting = [];
        private static readonly LinkedList<PathKey> Pending = new();
        private static readonly SemaphoreSlim Signal = new(0);
        private static bool _started;

        public static bool TryGet(PathKey key, out ImageSource? icon)
        {
            lock (Gate)
            {
                return Known.TryGetValue(key, out icon);
            }
        }

        public static void Request(PathKey key, Action<ImageSource> loaded)
        {
            lock (Gate)
            {
                if (Waiting.TryGetValue(key, out var callbacks))
                {
                    callbacks.Add(loaded);
                    return;
                }

                Waiting[key] = [loaded];
                Pending.AddFirst(key);
                while (Pending.Count > MaxPending)
                {
                    Waiting.Remove(Pending.Last!.Value);
                    Pending.RemoveLast();
                }

                if (!_started)
                {
                    _started = true;
                    var thread = new Thread(Work) { IsBackground = true, Name = "Icons", Priority = ThreadPriority.BelowNormal };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
            }

            Signal.Release();
        }

        private static void Work()
        {
            while (true)
            {
                Signal.Wait();
                PathKey key;
                lock (Gate)
                {
                    if (Pending.First is not { } first)
                    {
                        continue;
                    }

                    key = first.Value;
                    Pending.RemoveFirst();
                }

                ImageSource? icon = null;
                try
                {
                    // The bitmap is made (and frozen) here too, so the UI thread only swaps it in.
                    icon = FromIndex(ShellIcons.IconIndex(key.Path, key.IsDirectory, exists: true), key.Size);
                }
                catch (Exception)
                {
                    // A broken shell extension must not take the worker down; the generic icon stays.
                }

                List<Action<ImageSource>>? callbacks;
                lock (Gate)
                {
                    Waiting.Remove(key, out callbacks);
                    if (Known.Count >= MaxRemembered)
                    {
                        Known.Clear();
                    }

                    Known[key] = icon;
                }

                if (icon is not null && callbacks is { Count: > 0 } && Application.Current?.Dispatcher is { } dispatcher)
                {
                    dispatcher.BeginInvoke(() =>
                    {
                        foreach (var callback in callbacks)
                        {
                            callback(icon);
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }
    }

    public static ImageSource? Folder(double dips = 16) => FromIndex(ShellIcons.IconIndex("folder", true, exists: false), SizeFor(dips));

    private static ImageSource? FromIndex(int index, ShellIconSize size)
        => index < 0 ? null : ByIndex.GetOrAdd((index, size), key =>
        {
            var handle = ShellIcons.IconFromIndex(key.Index, key.Size);
            if (handle == 0)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                ShellIcons.DestroyIcon(handle);
            }
        });

    /// <summary>
    /// Startup: decodes the given logos and makes the shell's first icons on background threads
    /// while the window is being built, so the sidebar and the first listing find them ready.
    /// </summary>
    public static void Prewarm(IEnumerable<string> logoAssets)
    {
        var assets = logoAssets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _ = Task.Run(() =>
        {
            foreach (var asset in assets)
            {
                Logo(asset);
            }
        });

        var shell = new Thread(() =>
        {
            try
            {
                // The first SHGetFileInfo initialises the shell's image lists (~40 ms cold).
                foreach (var size in new[] { ShellIconSize.Large, ShellIconSize.ExtraLarge })
                {
                    FromIndex(ShellIcons.IconIndex("folder", true, exists: false), size);
                    FromIndex(ShellIcons.IconIndex("file.txt", false, exists: false), size);
                }
            }
            catch (Exception)
            {
                // Only a head start; the UI thread makes them itself if this failed.
            }
        })
        { IsBackground = true, Name = "Icon prewarm" };
        shell.SetApartmentState(ApartmentState.STA);
        shell.Start();
    }

    /// <summary>The logo if it has been decoded already (never blocks).</summary>
    public static bool TryGetLogo(string assetName, out ImageSource? logo) => Logos.TryGetValue(assetName, out logo);

    /// <summary>The logo, decoded on the thread pool the first time.</summary>
    public static Task<ImageSource?> LogoAsync(string assetName)
        => TryGetLogo(assetName, out var logo) ? Task.FromResult(logo) : Task.Run(() => Logo(assetName));

    /// <summary>A provider logo from Assets/ProviderLogos/&lt;Name&gt;.png, or null.</summary>
    public static ImageSource? Logo(CloudProviderType type) => Logo(type.LogoAssetName());

    public static ImageSource? Logo(string assetName)
        => Logos.GetOrAdd(assetName, name =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "ProviderLogos", name + ".png");
            if (!File.Exists(path))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return TrimTransparentMargins(image);
        });

    /// <summary>
    /// The logo with its empty border (transparent or plain white) cut off. The artwork comes padded by very different
    /// amounts (Koofr fills two thirds of its canvas, most others all of it), so without this
    /// logos drawn into the same box look like different sizes.
    /// </summary>
    private static BitmapSource TrimTransparentMargins(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Faint anti-aliasing haze doesn't count as artwork, and neither does a plain
                // white canvas (the WebDAV, SFTP and Synology C2 logos are small marks on one).
                var i = (y * stride) + (x * 4);
                var isCanvas = pixels[i + 3] <= 16 || (pixels[i] >= 235 && pixels[i + 1] >= 235 && pixels[i + 2] >= 235);
                if (!isCanvas)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (right < left || (left == 0 && top == 0 && right == width - 1 && bottom == height - 1))
        {
            return source;
        }

        var cropped = new CroppedBitmap(bitmap, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// A logo sized for a grid of tiles: every logo covers about the same area, whatever its
    /// shape, so a wide wordmark and a square app icon look equally large; nothing exceeds
    /// <paramref name="box"/> in height or <paramref name="maxWidth"/> (default: the box) in width — wordmarks may run wider than the square.
    /// </summary>
    public static Image? LogoTile(CloudProviderType type, double box, double? maxWidth = null)
    {
        if (Logo(type) is not BitmapSource logo || logo.PixelWidth == 0 || logo.PixelHeight == 0)
        {
            return null;
        }

        double width = logo.PixelWidth;
        double height = logo.PixelHeight;

        // Same visual weight: a square logo fills ~80% of the box; wider ones trade height for width.
        var area = box * box * 0.64;
        var scale = Math.Sqrt(area / (width * height));
        width *= scale;
        height *= scale;

        // Then fit the box.
        var fit = Math.Min(1, Math.Min((maxWidth ?? box) / width, box / height));
        var image = new Image
        {
            Source = logo,
            Width = Math.Round(width * fit),
            Height = Math.Round(height * fit),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>
    /// A real thumbnail for a local file, or null (the caller keeps the icon). Blocks on the
    /// shell's thumbnail handlers — call it off the UI thread (see <see cref="ThumbnailCache"/>).
    /// </summary>
    public static ImageSource? Thumbnail(string path, int pixels)
    {
        var bitmap = ShellIcons.Thumbnail(path, pixels, thumbnailOnly: true);
        if (bitmap == 0)
        {
            return null;
        }

        try
        {
            var source = FromDibSection(bitmap) ?? Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            ShellIcons.DeleteObject(bitmap);
        }
    }

    /// <summary>
    /// The shell hands thumbnails over as 32-bit DIB sections with (premultiplied) alpha.
    /// <c>CreateBitmapSourceFromHBitmap</c> drops the alpha, which paints transparent PNGs
    /// and icons on black — so copy the pixels ourselves. Opaque formats (JPEG) come back with
    /// an all-zero alpha channel and are read as BGR. Null when the handle is not a 32-bit DIB.
    /// </summary>
    private static BitmapSource? FromDibSection(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<DibSection>(), out var dib) == 0 ||
            dib.Bitmap.Bits == 0 || dib.Bitmap.BitsPixel != 32 || dib.Bitmap.Width <= 0 || dib.Bitmap.Height <= 0)
        {
            return null;
        }

        var width = dib.Bitmap.Width;
        var height = dib.Bitmap.Height;
        var stride = dib.Bitmap.WidthBytes;
        var pixels = new byte[stride * height];
        var bottomUp = dib.Header.Height > 0;
        for (var row = 0; row < height; row++)
        {
            var sourceRow = bottomUp ? height - 1 - row : row;
            Marshal.Copy(dib.Bitmap.Bits + (sourceRow * stride), pixels, row * stride, stride);
        }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        return BitmapSource.Create(width, height, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(nint handle, int size, out DibSection dib);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public NativeBitmap Bitmap;
        public BitmapInfoHeader Header;
        public uint BitField0;
        public uint BitField1;
        public uint BitField2;
        public nint Section;
        public uint Offset;
    }
}

/// <summary>
/// Content thumbnails for local images and PDFs in the file list (macOS <c>ThumbnailCache</c>):
/// keyed by path + modification date + size so an edited file refreshes by itself, with
/// failures remembered so scrolling a big folder doesn't re-ask the shell for thumbnails it
/// already refused. Rows ask only while they are on screen (the list virtualizes), get the
/// type icon at once, and receive the thumbnail later on the UI thread.
///
/// <para>The shell's thumbnail handlers are COM objects that expect an STA, so a couple of
/// dedicated STA threads do the work. Requests are served newest first and only the most
/// recent ones are kept, so after a fast scroll the rows now on screen come first and those
/// scrolled past are dropped (they ask again if they come back).</para>
/// </summary>
public static class ThumbnailCache
{
    private const int MaxEntries = 512;
    private const int MaxPending = 160;
    private const int WorkerCount = 2;

    /// <summary>What Quick Look thumbnails on macOS: images and PDFs (UTType image / pdf).</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "webp", "heic", "heif", "avif",
        "ico", "jxr", "wdp", "jxl", "svg", "psd", "tga",
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "dng", "orf", "rw2", "raf", "srw", "pef", "erf", "kdc", "mrw", "sr2", "3fr",
        "pdf",
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, ImageSource> Cache = [];
    private static readonly Queue<Key> Order = new();
    private static readonly HashSet<Key> Failed = [];
    private static readonly Dictionary<Key, List<Action<ImageSource>>> Waiting = [];
    private static readonly LinkedList<(Key Key, string Path)> Pending = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static bool _started;

    /// <summary>Requested edge in pixels: the 16-DIP row icon at the screen's scale, at least 32.</summary>
    public static int PixelSize { get; set; } = 32;

    private readonly record struct Key(string Path, long ModifiedTicks, long Size);

    /// <summary>True when a content thumbnail beats the type icon for this item (local, not a placeholder).</summary>
    public static bool ShouldUseThumbnail(CloudFileItem item, bool isLocal)
        => isLocal && !item.IsDirectory && item.Path.Length > 0 && item.DownloadStatus != CloudDownloadStatus.Evicted &&
           Extensions.Contains(item.Extension);

    /// <summary>
    /// The thumbnail if it is ready; otherwise queues it and calls <paramref name="loaded"/> on
    /// the UI thread once it is. Returns null at once for files the shell couldn't thumbnail.
    /// </summary>
    public static ImageSource? Get(CloudFileItem item, Action<ImageSource> loaded)
    {
        var key = new Key(item.Path, item.ModificationDate.UtcTicks, item.Size);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
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
            Pending.AddFirst((key, item.Path));
            while (Pending.Count > MaxPending)
            {
                Waiting.Remove(Pending.Last!.Value.Key);
                Pending.RemoveLast();
            }

            StartWorkers();
        }

        Signal.Release();
        return null;
    }

    private static void StartWorkers()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        for (var i = 0; i < WorkerCount; i++)
        {
            var thread = new Thread(Work) { IsBackground = true, Name = "Thumbnails", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    private static void Work()
    {
        while (true)
        {
            Signal.Wait();
            Key key;
            string path;
            lock (Gate)
            {
                if (Pending.First is not { } first)
                {
                    continue;
                }

                (key, path) = first.Value;
                Pending.RemoveFirst();
            }

            ImageSource? image = null;
            try
            {
                image = IconCache.Thumbnail(path, PixelSize);
            }
            catch (Exception)
            {
                // A broken thumbnail handler must not take the worker down; the icon stays.
            }

            List<Action<ImageSource>>? callbacks;
            lock (Gate)
            {
                Waiting.Remove(key, out callbacks);
                if (image is null)
                {
                    if (Failed.Count > 20_000)
                    {
                        Failed.Clear();
                    }

                    Failed.Add(key);
                    continue;
                }

                Cache[key] = image;
                Order.Enqueue(key);
                while (Order.Count > MaxEntries)
                {
                    Cache.Remove(Order.Dequeue());
                }
            }

            var ready = image;
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
    }
}
