// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Archives;

/// <summary>
/// The archive kinds FileFluss can look inside (macOS <c>ArchiveFormat</c>).
///
/// <para>Recognition is by file extension, deliberately: the context menu and the
/// double-click handler both have to decide "is this an archive?" for a row the user just
/// clicked, and opening every clicked file to sniff its magic bytes would put I/O on the UI
/// thread. A file whose extension lies is caught when the archive is actually opened —
/// listing fails, and the caller falls back to handing the file to its default app.</para>
/// </summary>
public enum ArchiveFormat
{
    Zip,
    Rar,
    SevenZip,
    Tar,
    TarGzip,
    TarBzip2,
    TarXz,
    Gzip,
    Bzip2,
    Xz,
}

public static class ArchiveFormats
{
    /// <summary>Extensions that map to a format, longest first so ".tar.gz" wins over ".gz".</summary>
    private static readonly (string Suffix, ArchiveFormat Format)[] ExtensionTable =
    [
        (".tar.gz", ArchiveFormat.TarGzip), (".tgz", ArchiveFormat.TarGzip),
        (".tar.bz2", ArchiveFormat.TarBzip2), (".tbz", ArchiveFormat.TarBzip2), (".tbz2", ArchiveFormat.TarBzip2),
        (".tar.xz", ArchiveFormat.TarXz), (".txz", ArchiveFormat.TarXz),
        (".zip", ArchiveFormat.Zip), (".cbz", ArchiveFormat.Zip),
        (".rar", ArchiveFormat.Rar), (".cbr", ArchiveFormat.Rar),
        (".7z", ArchiveFormat.SevenZip),
        (".tar", ArchiveFormat.Tar),
        (".gz", ArchiveFormat.Gzip),
        (".bz2", ArchiveFormat.Bzip2),
        (".xz", ArchiveFormat.Xz),
    ];

    /// <summary>
    /// The format <paramref name="fileName"/> claims to be, or null. Container formats that are
    /// zips underneath — .docx, .epub, .nupkg — are <b>not</b> listed: double-clicking a Word
    /// document has to open Word; someone who wants to see inside one can rename it.
    /// </summary>
    public static ArchiveFormat? Detect(string fileName)
    {
        foreach (var (suffix, format) in ExtensionTable)
        {
            // ".zip" on its own is a hidden file called "zip", not an archive.
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>
    /// The name the extracted contents go under: the archive's name without its archive
    /// extension. "photos.tar.gz" → "photos".
    /// </summary>
    public static string BaseName(string archiveName)
    {
        foreach (var (suffix, _) in ExtensionTable)
        {
            if (archiveName.Length > suffix.Length && archiveName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return archiveName[..^suffix.Length];
            }
        }

        return archiveName;
    }

    /// <summary>One compressed stream rather than a directory of entries ("notes.txt.gz").</summary>
    public static bool IsSingleStream(this ArchiveFormat format) => format is ArchiveFormat.Gzip or ArchiveFormat.Bzip2 or ArchiveFormat.Xz;

    /// <summary>The tar family: entries in a tar stream, compressed or not.</summary>
    public static bool IsTar(this ArchiveFormat format) => format is ArchiveFormat.Tar or ArchiveFormat.TarGzip or ArchiveFormat.TarBzip2 or ArchiveFormat.TarXz;
}

/// <summary>
/// The kinds of archive FileFluss can <i>write</i> (macOS <c>ArchiveCreationFormat</c>). ZIP,
/// 7-Zip and the three tarballs are written in-process; RAR's compression is proprietary and
/// the only way to make one is RARLAB's own rar tool, so it is offered when that tool is
/// installed and explained when it isn't.
/// </summary>
public enum ArchiveCreationFormat
{
    Zip,
    SevenZip,
    TarGzip,
    TarBzip2,
    TarXz,
    Rar,
}

public static class ArchiveCreationFormats
{
    public static IReadOnlyList<ArchiveCreationFormat> All { get; } = Enum.GetValues<ArchiveCreationFormat>();

    public static string FileExtension(this ArchiveCreationFormat format) => format switch
    {
        ArchiveCreationFormat.Zip => "zip",
        ArchiveCreationFormat.SevenZip => "7z",
        ArchiveCreationFormat.TarGzip => "tar.gz",
        ArchiveCreationFormat.TarBzip2 => "tar.bz2",
        ArchiveCreationFormat.TarXz => "tar.xz",
        _ => "rar",
    };

    /// <summary>Menu wording. Format names, so not translated.</summary>
    public static string DisplayName(this ArchiveCreationFormat format) => format switch
    {
        ArchiveCreationFormat.Zip => "ZIP",
        ArchiveCreationFormat.SevenZip => "7-Zip (.7z)",
        ArchiveCreationFormat.TarGzip => "TAR.GZ",
        ArchiveCreationFormat.TarBzip2 => "TAR.BZ2",
        ArchiveCreationFormat.TarXz => "TAR.XZ",
        _ => "RAR",
    };

    /// <summary>Whether this format can be written now: RAR once its tool is found, TAR.XZ while Windows' tar.exe is there, the rest always.</summary>
    public static bool IsAvailable(this ArchiveCreationFormat format) => ArchiveWriter.CanCreate(format);

    /// <summary>
    /// The archive name for compressing <paramref name="itemNames"/>: one item gives
    /// "&lt;name&gt;.&lt;ext&gt;", several give "Archive.&lt;ext&gt;", like Finder — made unique
    /// against <paramref name="isTaken"/> as "Archive 2.tar.gz", not the "Archive.tar 2.gz" a
    /// single-extension rule would produce.
    /// </summary>
    public static string ArchiveName(this ArchiveCreationFormat format, IReadOnlyList<string> itemNames, Func<string, bool> isTaken)
    {
        var stem = itemNames.Count == 1 ? itemNames[0] : "Archive";
        var extension = format.FileExtension();
        var first = $"{stem}.{extension}";
        if (!isTaken(first))
        {
            return first;
        }

        var counter = 2;
        while (isTaken($"{stem} {counter}.{extension}"))
        {
            counter++;
        }

        return $"{stem} {counter}.{extension}";
    }
}
