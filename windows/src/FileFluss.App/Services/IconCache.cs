// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
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
            image.DecodePixelWidth = 64;
            image.EndInit();
            image.Freeze();
            return image;
        });

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
