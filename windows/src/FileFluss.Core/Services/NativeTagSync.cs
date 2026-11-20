// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using FileFluss.Core.Models;

namespace FileFluss.Core.Services;

/// <summary>What a cloud service can hold of an item's tags itself (macOS <c>NativeTagSupport</c>).</summary>
public enum NativeTagSupport
{
    /// <summary>Nothing: the tags are FileFluss's own record only.</summary>
    None,

    /// <summary>One colour label per item and no names (MEGA).</summary>
    OneColor,

    /// <summary>Any number of named, coloured labels per item (kDrive's categories).</summary>
    Full,
}

/// <summary>
/// Keeps FileFluss's record of an item's tags and the service's own labels in step, for the
/// services that have labels (macOS <c>NativeTagSync</c>).
///
/// <para>Both sides can change: a tag is given in FileFluss, or a label in the service's own
/// app. When a folder is listed the two are compared, and the rule is short. The service wins
/// — what its own apps show is what the user last did there — except for an item FileFluss has
/// only just written (an upload, a copy from another account), whose tags the service can't
/// know yet: those are sent to it.</para>
/// </summary>
public static class NativeTagSync
{
    /// <param name="Tags">The item's tags after comparing the two sides.</param>
    /// <param name="Push">What to write to the service, or null when it already agrees.</param>
    public sealed record Outcome(IReadOnlyList<FileTag> Tags, IReadOnlyList<FileTag>? Push);

    /// <summary>What the service is asked to hold for these tags: everything where it can, otherwise the last colour — the one a folder is shown in.</summary>
    public static IReadOnlyList<FileTag> NativeForm(IReadOnlyList<FileTag> tags, NativeTagSupport support) => support switch
    {
        NativeTagSupport.Full => tags,
        NativeTagSupport.OneColor => tags.LastOrDefault(t => t.Color != TagColor.None) is { } last ? [last] : [],
        _ => [],
    };

    /// <param name="stored">FileFluss's record for the item.</param>
    /// <param name="native">The labels the service has for it.</param>
    /// <param name="support">What the service can hold.</param>
    /// <param name="pending">The record was written by a transfer and the service hasn't been told yet.</param>
    /// <param name="standard">The seven colour tags, for a colour that arrives from the service without a name.</param>
    public static Outcome Reconcile(
        IReadOnlyList<FileTag> stored,
        IReadOnlyList<FileTag> native,
        NativeTagSupport support,
        bool pending,
        IReadOnlyList<FileTag> standard)
    {
        switch (support)
        {
            case NativeTagSupport.OneColor:
            {
                var nativeColor = native.FolderTint();
                if (stored.FolderTint() == nativeColor)
                {
                    return new Outcome(stored, null);
                }

                if (pending)
                {
                    return new Outcome(stored, NativeForm(stored, support));
                }

                if (nativeColor is not { } color)
                {
                    // The label was taken off elsewhere: the colours go, the tags without one stay.
                    return new Outcome(stored.Where(t => t.Color == TagColor.None).ToList(), null);
                }

                // Another colour was chosen elsewhere. It becomes the last tag, under the name
                // it already has here if it has one.
                var tags = stored.ToList();
                FileTag tag;
                var index = tags.FindLastIndex(t => t.Color == color);
                if (index >= 0)
                {
                    tag = tags[index];
                    tags.RemoveAt(index);
                }
                else
                {
                    tag = standard.FirstOrDefault(t => t.Color == color) ?? new FileTag(color.EnglishName(), color);
                    tags.RemoveAll(t => string.Equals(t.Name, tag.Name, StringComparison.Ordinal));
                }

                tags.Add(tag);
                return new Outcome(tags, null);
            }

            case NativeTagSupport.Full:
            {
                if (!pending)
                {
                    return new Outcome(native, null);
                }

                var known = new HashSet<string>(native.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                var merged = native.Concat(stored.Where(t => !known.Contains(t.Name))).ToList();
                return new Outcome(merged, merged.SameAs(native) ? null : merged);
            }

            default:
                return new Outcome(stored, null);
        }
    }

    /// <summary>The tag colour nearest to an arbitrary one, for a service whose labels can be any colour (<c>#RRGGBB</c>). Unreadable colours are grey.</summary>
    public static TagColor NearestColor(string? hex)
    {
        if (Rgb(hex) is not { } wanted)
        {
            return TagColor.Gray;
        }

        var best = TagColor.Gray;
        var bestDistance = int.MaxValue;
        foreach (var color in FileTags.MenuOrder)
        {
            var (r, g, b) = color.Rgb();
            int dr = wanted.R - r, dg = wanted.G - g, db = wanted.B - b;
            var distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = color;
            }
        }

        return best;
    }

    /// <summary>The colour FileFluss gives a label it creates on such a service.</summary>
    public static string HexColor(TagColor color)
    {
        var (r, g, b) = (color == TagColor.None ? TagColor.Gray : color).Rgb();
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    private static (int R, int G, int B)? Rgb(string? hex)
    {
        var text = (hex ?? string.Empty).Trim().TrimStart('#');
        if (text.Length == 3)
        {
            text = string.Concat(text.Select(c => $"{c}{c}"));
        }

        return text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)
            : null;
    }
}
