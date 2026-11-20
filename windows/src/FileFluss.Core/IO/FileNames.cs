// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;

namespace FileFluss.Core.IO;

/// <summary>
/// Matching file names the way a person reads them.
///
/// <para>A Mac writes names in decomposed Unicode (NFD: "e" + combining acute), Windows and
/// most web apps in composed form (NFC: "é"). Many clouds store whatever arrived, so a folder
/// created on a Mac and one created on a PC can hold "Café" twice — byte-different, pixel-
/// identical. NTFS keeps both as separate files, so a name match that compares code units
/// misses the clash: a "Replace" lands beside the old file instead of over it, and a mirror
/// sync copies the file and then deletes the copy as "extra". Matches therefore compare the
/// NFC form; the name actually written keeps the spelling it came with.</para>
/// </summary>
public static class FileNames
{
    private static readonly StringComparer Sensitive = new NormalizingComparer(StringComparer.Ordinal);
    private static readonly StringComparer Insensitive = new NormalizingComparer(StringComparer.OrdinalIgnoreCase);

    /// <summary>The composed (NFC) form, the key every match uses. Cheap for plain ASCII.</summary>
    public static string Normalize(string name)
    {
        try
        {
            return name.IsNormalized(NormalizationForm.FormC) ? name : name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // An unpaired surrogate from a broken server name: compare it as it is.
            return name;
        }
    }

    /// <summary>
    /// Compares names (or relative paths) normalization-insensitively, and case-insensitively
    /// when <paramref name="ignoreCase"/> — pass true when the place they land folds case.
    /// </summary>
    public static StringComparer MatchComparer(bool ignoreCase) => ignoreCase ? Insensitive : Sensitive;

    private sealed class NormalizingComparer(StringComparer inner) : StringComparer
    {
        public override int Compare(string? x, string? y)
            => inner.Compare(x is null ? null : Normalize(x), y is null ? null : Normalize(y));

        public override bool Equals(string? x, string? y)
            => ReferenceEquals(x, y) || (x is not null && y is not null && inner.Equals(Normalize(x), Normalize(y)));

        public override int GetHashCode(string obj) => inner.GetHashCode(Normalize(obj));
    }
}
