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

    public static ImageSource? For(CloudFileItem item, bool isLocal, double dips = 16)
    {
        var exists = isLocal && (item.IsDirectory || ShellIcons.NeedsPerFileIcon(item.Name)) && item.Path.Length > 0;
        var index = ShellIcons.IconIndex(exists ? item.Path : item.Name, item.IsDirectory, exists);
        return FromIndex(index, SizeFor(dips));
    }

    public static ImageSource? ForPath(string path, bool isDirectory, double dips = 16)
        => FromIndex(ShellIcons.IconIndex(path, isDirectory, exists: true), SizeFor(dips));

    public static ImageSource? Folder(double dips = 16) => FromIndex(ShellIcons.IconIndex("folder", true, exists: false), SizeFor(dips));

    /// <summary>
    /// Programs, shortcuts and .ico files each bring their own icon (and index), so browsing
    /// folders full of them would grow the cache without end; past this many it starts over.
    /// </summary>
    private const int MaxIcons = 2048;

    private static ImageSource? FromIndex(int index, ShellIconSize size)
    {
        if (index < 0)
        {
            return null;
        }

        if (ByIndex.Count > MaxIcons && !ByIndex.ContainsKey((index, size)))
        {
            ByIndex.Clear();
        }

        return ByIndex.GetOrAdd((index, size), key =>
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
            catch (Exception e) when (e is COMException or ArgumentException)
            {
                // A damaged icon: the row goes without one rather than failing to draw.
                return null;
            }
            finally
            {
                ShellIcons.DestroyIcon(handle);
            }
        });
    }

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

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return TrimTransparentMargins(image);
            }
            catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or COMException or FileFormatException)
            {
                // A damaged install: the glyph stands in, as for a provider without a logo.
                return null;
            }
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
