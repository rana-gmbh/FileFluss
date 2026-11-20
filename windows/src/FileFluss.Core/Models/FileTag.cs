// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Models;

/// <summary>
/// The colour of a tag (macOS <c>TagColor</c>). The numbers are the indexes Finder stores, so
/// a tag file written here and one written by the Mac app mean the same thing.
/// </summary>
public enum TagColor
{
    None = 0,
    Gray = 1,
    Green = 2,
    Purple = 3,
    Blue = 4,
    Yellow = 5,
    Red = 6,
    Orange = 7,
}

/// <summary>
/// One tag on a file or folder: a name and, usually, a colour. The name is what identifies a
/// tag, as in Finder: an item has "Red" or it hasn't, and two tags of one colour with
/// different names are two tags.
/// </summary>
public sealed record FileTag(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] TagColor Color);

public static class FileTags
{
    public static readonly IReadOnlyList<FileTag> None = [];

    /// <summary>The seven colours in the order Finder offers them.</summary>
    public static readonly IReadOnlyList<TagColor> MenuOrder =
        [TagColor.Red, TagColor.Orange, TagColor.Yellow, TagColor.Green, TagColor.Blue, TagColor.Purple, TagColor.Gray];

    /// <summary>
    /// The seven colour tags the menu offers. Windows has no tag names of its own to borrow,
    /// as the Mac app borrows Finder's, so the stored name is the English one whatever the
    /// language: a tag set in German is still found after switching to English. What the user
    /// reads is <see cref="DisplayKey"/>, translated.
    /// </summary>
    public static readonly IReadOnlyList<FileTag> Standard = MenuOrder.Select(color => new FileTag(color.EnglishName(), color)).ToList();

    public static string EnglishName(this TagColor color) => color switch
    {
        TagColor.Gray => "Gray",
        TagColor.Green => "Green",
        TagColor.Purple => "Purple",
        TagColor.Blue => "Blue",
        TagColor.Yellow => "Yellow",
        TagColor.Red => "Red",
        TagColor.Orange => "Orange",
        _ => string.Empty,
    };

    /// <summary>One of the seven, under its own name — the tags whose name is shown translated.</summary>
    public static bool IsStandard(this FileTag tag)
        => tag.Color != TagColor.None && string.Equals(tag.Name, tag.Color.EnglishName(), StringComparison.Ordinal);

    /// <summary>The colour drawn for the tag, the same in light and dark mode.</summary>
    public static (byte R, byte G, byte B) Rgb(this TagColor color) => color switch
    {
        TagColor.Gray => (0x8E, 0x8E, 0x93),
        TagColor.Green => (0x34, 0xC7, 0x59),
        TagColor.Purple => (0xAF, 0x52, 0xDE),
        TagColor.Blue => (0x1E, 0x88, 0xFF),
        TagColor.Yellow => (0xFF, 0xCC, 0x00),
        TagColor.Red => (0xFF, 0x45, 0x3A),
        TagColor.Orange => (0xFF, 0x95, 0x00),
        _ => (0, 0, 0),
    };

    /// <summary>A colour index read from a file, made one this version knows.</summary>
    public static TagColor Known(TagColor color) => Enum.IsDefined(color) ? color : TagColor.None;

    /// <summary>The colours to draw as dots, in the order the tags were given. Tags without a colour have no dot.</summary>
    public static IReadOnlyList<TagColor> DotColors(this IReadOnlyList<FileTag> tags)
        => tags.Count == 0 ? [] : tags.Where(t => t.Color != TagColor.None).Select(t => t.Color).ToList();

    /// <summary>The colour a folder takes: that of its last coloured tag, as in Finder.</summary>
    public static TagColor? FolderTint(this IReadOnlyList<FileTag> tags)
    {
        for (var i = tags.Count - 1; i >= 0; i--)
        {
            if (tags[i].Color != TagColor.None)
            {
                return tags[i].Color;
            }
        }

        return null;
    }

    public static bool SameAs(this IReadOnlyList<FileTag> tags, IReadOnlyList<FileTag> other) => tags.SequenceEqual(other);
}

/// <summary>A change the Tags menu makes to an item's tags (macOS <c>TagChange</c>).</summary>
public abstract record TagChange
{
    private TagChange()
    {
    }

    public sealed record Add(FileTag Tag) : TagChange;

    public sealed record Remove(string Name) : TagChange;

    public sealed record RemoveAll : TagChange;

    /// <summary>The tags an item has after the change. Adding puts the tag last, so the newest colour is the one a folder shows.</summary>
    public IReadOnlyList<FileTag> Apply(IReadOnlyList<FileTag> tags) => this switch
    {
        Add add => tags.Any(t => string.Equals(t.Name, add.Tag.Name, StringComparison.Ordinal)) ? tags : [.. tags, add.Tag],
        Remove remove => tags.Where(t => !string.Equals(t.Name, remove.Name, StringComparison.Ordinal)).ToList(),
        _ => [],
    };
}

/// <summary>
/// What the Tags menu shows for a selection: every tag on offer, and whether all, some or
/// none of the selected items have it (macOS <c>TagMenuModel</c>).
/// </summary>
public static class TagMenuModel
{
    public enum Presence
    {
        All,
        Some,
        None,
    }

    public sealed record Row(FileTag Tag, Presence Presence)
    {
        /// <summary>What choosing the row does: a tag every item has is taken away, anything else is given to the items still missing it.</summary>
        public TagChange Change => Presence == Presence.All ? new TagChange.Remove(Tag.Name) : new TagChange.Add(Tag);
    }

    /// <summary>
    /// The standard tags first, in the order given, then any other tags the selection carries
    /// — which can be taken off here but not created.
    /// </summary>
    public static IReadOnlyList<Row> Rows(IReadOnlyList<FileTag> standard, IReadOnlyList<IReadOnlyList<FileTag>> selection)
    {
        var seen = new HashSet<string>(standard.Select(t => t.Name), StringComparer.Ordinal);
        var all = new List<FileTag>(standard);
        foreach (var tags in selection)
        {
            foreach (var tag in tags)
            {
                if (seen.Add(tag.Name))
                {
                    all.Add(tag);
                }
            }
        }

        var rows = new List<Row>(all.Count);
        foreach (var tag in all)
        {
            var count = selection.Count(tags => tags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.Ordinal)));
            var presence = count == 0 ? Presence.None : count == selection.Count ? Presence.All : Presence.Some;
            rows.Add(new Row(tag, presence));
        }

        return rows;
    }

    /// <summary>Whether there is anything for "Remove All Tags" to remove.</summary>
    public static bool HasAnyTag(IReadOnlyList<IReadOnlyList<FileTag>> selection) => selection.Any(tags => tags.Count > 0);
}

/// <summary>The dots beside a name: the last three colours, overlapping as in Finder (macOS <c>TagDotsView</c>).</summary>
public static class TagDotsLayout
{
    public const double Diameter = 9;

    public const double Step = 5;

    public const int Maximum = 3;

    public static double Width(int count)
    {
        var shown = Math.Min(count, Maximum);
        return shown <= 0 ? 0 : Diameter + ((shown - 1) * Step);
    }

    /// <summary>The colours actually drawn: the newest is last, on the right and on top.</summary>
    public static IReadOnlyList<TagColor> Shown(IReadOnlyList<TagColor> colors)
        => colors.Count <= Maximum ? colors : colors.Skip(colors.Count - Maximum).ToList();
}

/// <summary>
/// Recolours a folder icon like its tag, keeping the icon's shading: each pixel is drained of
/// its own colour and overlaid with the tint, the way the Mac app draws a tagged folder.
/// </summary>
public static class TagTint
{
    /// <summary>
    /// Recolours 32-bit BGRA pixels (straight or premultiplied alpha) in place. Transparent
    /// pixels stay transparent, so the icon keeps its shape.
    /// </summary>
    public static void Recolour(Span<byte> bgra, TagColor color, bool premultiplied = true)
    {
        if (color == TagColor.None)
        {
            return;
        }

        var (red, green, blue) = color.Rgb();
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            if (alpha == 0)
            {
                continue;
            }

            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (premultiplied)
            {
                b = b * 255 / alpha;
                g = g * 255 / alpha;
                r = r * 255 / alpha;
            }

            // Luminance stands in for the icon's shading.
            var light = ((0.299 * r) + (0.587 * g) + (0.114 * b)) / 255;
            var nb = Overlay(light, blue / 255.0);
            var ng = Overlay(light, green / 255.0);
            var nr = Overlay(light, red / 255.0);
            var scale = premultiplied ? alpha : 255;
            bgra[i] = (byte)Math.Clamp(Math.Round(nb * scale), 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(Math.Round(ng * scale), 0, 255);
            bgra[i + 2] = (byte)Math.Clamp(Math.Round(nr * scale), 0, 255);
        }
    }

    /// <summary>The "overlay" blend: dark shading darkens the tint, light shading lightens it.</summary>
    private static double Overlay(double backdrop, double tint)
        => backdrop < 0.5 ? 2 * backdrop * tint : 1 - (2 * (1 - backdrop) * (1 - tint));
}
