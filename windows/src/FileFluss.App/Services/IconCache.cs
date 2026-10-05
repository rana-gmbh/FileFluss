// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.IO;
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

    /// <summary>A real thumbnail for a local image/PDF/video, or null (the caller keeps the icon).</summary>
    public static ImageSource? Thumbnail(string path, int pixels)
    {
        var bitmap = ShellIcons.Thumbnail(path, pixels, thumbnailOnly: true);
        if (bitmap == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            ShellIcons.DeleteObject(bitmap);
        }
    }
}
