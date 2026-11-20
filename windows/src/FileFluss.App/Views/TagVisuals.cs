// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FileFluss.Core.Models;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>The colours and names of tags, as the panels and menus show them.</summary>
public static class TagVisuals
{
    private static readonly Dictionary<TagColor, SolidColorBrush> Brushes = [];

    /// <summary>A separating ring between overlapping dots, so two dots of similar colour stay two dots.</summary>
    private static readonly Pen Ring = FrozenPen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

    public static SolidColorBrush Brush(TagColor color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
        {
            var (r, g, b) = color.Rgb();
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            Brushes[color] = brush;
        }

        return brush;
    }

    public static Pen DotRing => Ring;

    /// <summary>
    /// What a tag is called on screen. The seven colour tags are stored under their English
    /// names (see <see cref="FileTags.Standard"/>) and shown translated; any other tag is
    /// shown as it was named.
    /// </summary>
    public static string DisplayName(FileTag tag)
    {
        if (!tag.IsStandard())
        {
            return tag.Name;
        }

        return tag.Color switch
        {
            TagColor.Red => L("Red"),
            TagColor.Orange => L("Orange"),
            TagColor.Yellow => L("Yellow"),
            TagColor.Green => L("Green"),
            TagColor.Blue => L("Blue"),
            TagColor.Purple => L("Purple"),
            TagColor.Gray => L("Gray"),
            _ => tag.Name,
        };
    }

    /// <summary>A dot for a menu row; a ring for a tag without a colour.</summary>
    public static UIElement MenuDot(TagColor color)
    {
        var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        if (color == TagColor.None)
        {
            dot.SetResourceReference(Shape.StrokeProperty, SystemColors.GrayTextBrushKey);
            dot.StrokeThickness = 1;
        }
        else
        {
            dot.Fill = Brush(color);
        }

        return dot;
    }

    /// <summary>A menu header of a dot and the tag's name.</summary>
    public static object MenuHeader(FileTag tag)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(MenuDot(tag.Color));
        panel.Children.Add(new TextBlock { Text = DisplayName(tag), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static Pen FrozenPen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }
}

/// <summary>
/// The coloured dots beside a name, overlapping as in Finder: the last three, the newest on
/// the right and on top (macOS <c>TagDotsView</c>). Drawn directly rather than built from
/// elements, because every row of a long list has one.
/// </summary>
public sealed class TagDots : FrameworkElement
{
    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(
        nameof(Colors),
        typeof(IReadOnlyList<TagColor>),
        typeof(TagDots),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public TagDots()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<TagColor>? Colors
    {
        get => (IReadOnlyList<TagColor>?)GetValue(ColorsProperty);
        set => SetValue(ColorsProperty, value);
    }

    /// <summary>Room kept after the dots, only while there are any — so an untagged name stays centred.</summary>
    public double Gap { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Colors?.Count ?? 0;
        return count == 0 ? default : new Size(TagDotsLayout.Width(count) + Gap, TagDotsLayout.Diameter);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Colors is not { Count: > 0 } colors)
        {
            return;
        }

        var shown = TagDotsLayout.Shown(colors);
        var radius = TagDotsLayout.Diameter / 2;
        var y = Math.Max(radius, RenderSize.Height / 2);
        for (var i = 0; i < shown.Count; i++)
        {
            var center = new Point(radius + (i * TagDotsLayout.Step), y);
            drawingContext.DrawEllipse(TagVisuals.Brush(shown[i]), i > 0 ? TagVisuals.DotRing : null, center, radius, radius);
        }
    }
}

/// <summary>A folder icon in its tag's colour, as Finder shows a tagged folder.</summary>
public static class TagIconTint
{
    /// <summary>Tinted copies, kept for as long as the icon they were made from.</summary>
    private static readonly ConditionalWeakTable<ImageSource, Dictionary<TagColor, ImageSource>> Cache = [];

    /// <summary><paramref name="icon"/> recoloured, or itself when it can't be (not a bitmap).</summary>
    public static ImageSource Apply(ImageSource icon, TagColor color)
    {
        if (color == TagColor.None || icon is not BitmapSource bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
        {
            return icon;
        }

        var tints = Cache.GetOrCreateValue(icon);
        lock (tints)
        {
            if (tints.TryGetValue(color, out var known))
            {
                return known;
            }

            ImageSource tinted;
            try
            {
                var source = bitmap.Format == PixelFormats.Pbgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
                var stride = source.PixelWidth * 4;
                var pixels = new byte[stride * source.PixelHeight];
                source.CopyPixels(pixels, stride, 0);
                TagTint.Recolour(pixels, color, premultiplied: true);
                var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
                result.Freeze();
                tinted = result;
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException or OutOfMemoryException)
            {
                // An icon that can't be read back is shown as it is; the dots still say it is tagged.
                tinted = icon;
            }

            tints[color] = tinted;
            return tinted;
        }
    }
}
