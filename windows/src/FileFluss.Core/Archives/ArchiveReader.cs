// Copyright (C) 2026 Rana GmbH â€” GPLv3. See LICENSE at the repository root.

using System.Formats.Tar;
using System.Globalization;
using GZipStream = System.IO.Compression.GZipStream;
using System.Text;
using FileFluss.Core.Diagnostics;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;
using CompressionMode = SharpCompress.Compressors.CompressionMode;

namespace FileFluss.Core.Archives;

/// <summary>One header from an archive, as <see cref="ArchiveReader.Walk"/> hands it over.</summary>
internal sealed class ArchiveRecord
{
    /// <summary>The entry, or null for a header that exists but isn't shown: AppleDouble metadata, a refused path, a device node.</summary>
    public ArchiveEntry? Entry { get; init; }

    /// <summary>The declared name of an entry left out for being unsafe.</summary>
    public string? RefusedName { get; init; }

    public string? LinkTarget { get; init; }

    /// <summary>A tar hard link: no data of its own, the same file as <see cref="LinkTarget"/>.</summary>
    public bool IsHardLink { get; init; }

    /// <summary>The entry's data, valid only while the visitor runs. Null when walking headers only.</summary>
    public Func<Stream>? OpenData { get; init; }
}

/// <summary>
/// Reads archives (the reading half of macOS <c>ArchiveService</c>, which used the system
/// libarchive). ZIP, RAR and 7z go through SharpCompress; the tar family through .NET's own
/// <see cref="TarReader"/> over a decompressing stream (GZip from .NET, BZip2 and XZ from
/// SharpCompress). Everything is a stream: no entry is ever read into memory whole.
/// </summary>
public static class ArchiveReader
{
    /// <summary>256 KB: big enough that per-call overhead disappears, small enough that progress moves and a cancel is noticed promptly.</summary>
    public const int BlockSize = 256 * 1024;

    private static int _encodingsRegistered;

    /// <summary>
    /// Reads the entry list. One pass over the headers: for a zip a seek to the central
    /// directory, for RAR and 7z their headers, for a tarball a walk through the stream.
    /// </summary>
    public static ArchiveTree List(string path, ArchiveFormat format, string? archiveName = null, CancellationToken cancellationToken = default)
    {
        var raw = new List<ArchiveEntry>();
        string? firstUnsafe = null;
        var unsafeCount = 0;
        Walk(path, format, archiveName ?? Path.GetFileName(path), withData: false, record =>
        {
            if (record.Entry is { } entry)
            {
                raw.Add(entry);
                if (entry.IsSymlink && ArchiveEntry.LinkEscapes(record.LinkTarget))
                {
                    firstUnsafe ??= entry.Path;
                    unsafeCount++;
                }
            }
            else if (record.RefusedName is { } name)
            {
                SupportLogger.Shared.Log($"skipped unsafe archive entry \"{name}\" in {Path.GetFileName(path)}", "archive", LogLevel.Error);
                firstUnsafe ??= name;
                unsafeCount++;
            }

            return true;
        }, cancellationToken);
        return new ArchiveTree(raw, firstUnsafe, unsafeCount);
    }

    /// <summary>Whether a RAR or 7z is solid (one compressed stream for many files). False for anything else, or when unreadable.</summary>
    public static bool IsSolid(string path, ArchiveFormat format)
    {
        if (format is not (ArchiveFormat.Rar or ArchiveFormat.SevenZip))
        {
            return false;
        }

        try
        {
            var options = new ReaderOptions { ArchiveEncoding = LegacyNameEncoding() };
            using IArchive archive = format == ArchiveFormat.Rar ? RarArchive.OpenArchive(path, options) : SevenZipArchive.OpenArchive(path, options);
            return archive.IsSolid;
        }
        catch (Exception e) when (Translate(e) is not null)
        {
            return false;
        }
    }

    /// <summary>
    /// Calls <paramref name="visit"/> for every header, in the order the archive stores them,
    /// until it returns false. With <paramref name="withData"/>, a solid RAR or 7z is read
    /// through once â€” there is no cheaper way to reach its tenth entry than through the first
    /// nine â€” and every other archive opens each entry where it lies.
    /// </summary>
    internal static void Walk(string path, ArchiveFormat format, string archiveName, bool withData, Func<ArchiveRecord, bool> visit, CancellationToken cancellationToken)
    {
        try
        {
            switch (format)
            {
                case ArchiveFormat.Zip or ArchiveFormat.Rar or ArchiveFormat.SevenZip:
                    WalkIndexed(path, format, withData, visit, cancellationToken);
                    break;
                case ArchiveFormat.Tar:
                    using (var file = OpenRead(path))
                    {
                        WalkTar(file, withData, visit, cancellationToken);
                    }

                    break;
                default:
                    WalkCompressed(path, format, archiveName, withData, visit, cancellationToken);
                    break;
            }
        }
        catch (Exception e) when (Translate(e) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// What a reader's failure means for the user: a password, or a file that isn't the
    /// archive it claims to be. Null for everything else (cancellation, a full disk while
    /// writing), which passes through unchanged.
    /// </summary>
    private static ArchiveException? Translate(Exception e) => e switch
    {
        OperationCanceledException or ArchiveException => null,
        CryptographicException => ArchiveException.Encrypted(),
        InvalidDataException or EndOfStreamException or FormatException or NotSupportedException or InvalidOperationException
            or IndexOutOfRangeException or ArgumentException or OverflowException => ArchiveException.Unreadable(e.Message, e),
        _ when e.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true => ArchiveException.Unreadable(e.Message, e),
        _ => null,
    };

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BlockSize, FileOptions.SequentialScan);

    // MARK: - ZIP, RAR, 7z

    private static void WalkIndexed(string path, ArchiveFormat format, bool withData, Func<ArchiveRecord, bool> visit, CancellationToken cancellationToken)
    {
        var options = new ReaderOptions { ArchiveEncoding = LegacyNameEncoding() };
        using IArchive archive = format switch
        {
            ArchiveFormat.Zip => ZipArchive.OpenArchive(path, options),
            ArchiveFormat.Rar => RarArchive.OpenArchive(path, options),
            _ => SevenZipArchive.OpenArchive(path, options),
        };

        if (withData && archive.IsSolid)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = reader;
                if (!visit(Record(reader.Entry, () => current.OpenEntryStream())))
                {
                    return;
                }
            }

            return;
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visit(Record(entry, withData ? entry.OpenEntryStream : null)))
            {
                return;
            }
        }
    }

    /// <summary>
    /// How names without the zip "UTF-8" flag are decoded: the system's OEM code page, as
    /// Explorer's own zip folders do â€” that is what Windows tools wrote them in.
    /// </summary>
    private static ArchiveEncoding LegacyNameEncoding()
    {
        if (Interlocked.Exchange(ref _encodingsRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        Encoding oem;
        try
        {
            oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            oem = Encoding.GetEncoding(437);
        }

        return new ArchiveEncoding { Default = oem };
    }

    private static ArchiveRecord Record(IEntry entry, Func<Stream>? open)
    {
        var key = entry.Key;
        if (string.IsNullOrEmpty(key))
        {
            return new ArchiveRecord();
        }

        if (ArchiveEntry.Normalise(key) is not { } path)
        {
            return new ArchiveRecord { RefusedName = key };
        }

        if (ArchiveEntry.IsAppleDouble(path))
        {
            return new ArchiveRecord();
        }

        var link = string.IsNullOrEmpty(entry.LinkTarget) ? null : entry.LinkTarget;
        var isDirectory = entry.IsDirectory;
        return new ArchiveRecord
        {
            Entry = new ArchiveEntry(path, isDirectory, link is not null && !isDirectory, isDirectory ? 0 : Math.Max(0, entry.Size), Date(entry.LastModifiedTime), entry.IsEncrypted),
            LinkTarget = link,
            OpenData = open,
        };
    }

    /// <summary>Zip and RAR store local times without a zone (DOS dates); a missing date reads as unknown.</summary>
    private static DateTimeOffset Date(DateTime? value)
    {
        if (value is not { } date || date.Year < 1971)
        {
            return Models.CloudFileItem.UnknownDate;
        }

        try
        {
            return date.Kind == DateTimeKind.Utc ? new DateTimeOffset(date) : new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Models.CloudFileItem.UnknownDate;
        }
    }

    // MARK: - The tar family

    private static void WalkTar(Stream stream, bool withData, Func<ArchiveRecord, bool> visit, CancellationToken cancellationToken)
    {
        using var reader = new TarReader(stream, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visit(Record(entry, withData)))
            {
                return;
            }
        }
    }

    private static ArchiveRecord Record(TarEntry entry, bool withData)
    {
        bool isDirectory = false, isLink = false, isHardLink = false;
        switch (entry.EntryType)
        {
            case TarEntryType.Directory:
                isDirectory = true;
                break;
            case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                break;
            case TarEntryType.SymbolicLink:
                isLink = true;
                break;
            case TarEntryType.HardLink:
                isLink = isHardLink = true;
                break;
            default:
                // Global headers, devices, FIFOs: nothing a user put there to be unpacked.
                return new ArchiveRecord();
        }

        var name = entry.Name;
        if (string.IsNullOrEmpty(name))
        {
            return new ArchiveRecord();
        }

        if (ArchiveEntry.Normalise(name) is not { } path)
        {
            return new ArchiveRecord { RefusedName = name };
        }

        if (ArchiveEntry.IsAppleDouble(path))
        {
            return new ArchiveRecord();
        }

        var date = entry.ModificationTime.Year < 1971 ? Models.CloudFileItem.UnknownDate : entry.ModificationTime;
        var link = isLink ? entry.LinkName : null;
        return new ArchiveRecord
        {
            Entry = new ArchiveEntry(path, isDirectory, isLink, isDirectory || isLink ? 0 : Math.Max(0, entry.Length), date, false),

            // A hard link names another entry inside the archive; the escape check applies to
            // what it names, so it is normalised here like any entry path.
            LinkTarget = isHardLink ? ArchiveEntry.Normalise(link ?? string.Empty) ?? "/" : link,
            IsHardLink = isHardLink,
            OpenData = withData ? () => entry.DataStream ?? Stream.Null : null,
        };
    }

    /// <summary>
    /// A compressed tarball, or a single compressed file. The single-stream extensions (.gz,
    /// .bz2, .xz) are sniffed rather than trusted: a tarball misnamed "backup.gz" still reads
    /// as the tar it is, while "notes.txt.gz" is one nameless stream that takes the archive's
    /// name minus its extension.
    /// </summary>
    private static void WalkCompressed(string path, ArchiveFormat format, string archiveName, bool withData, Func<ArchiveRecord, bool> visit, CancellationToken cancellationToken)
    {
        using var file = OpenRead(path);
        using var decompressed = Decompress(file, format);
        if (format.IsTar())
        {
            WalkTar(decompressed, withData, visit, cancellationToken);
            return;
        }

        var head = new byte[512];
        var count = decompressed.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (count == head.Length && LooksLikeTar(head))
        {
            using var replay = new PrefixedStream(head, decompressed);
            WalkTar(replay, withData, visit, cancellationToken);
            return;
        }

        var name = ArchiveFormats.BaseName(archiveName);
        if (ArchiveEntry.Normalise(name) is not { } entryPath)
        {
            visit(new ArchiveRecord { RefusedName = name });
            return;
        }

        // The size is known for short streams (all read already) and for gzip, whose trailer
        // carries it (modulo 4 GiB); other streams don't say until decompressed.
        long size = count < head.Length ? count : format == ArchiveFormat.Gzip ? GzipDeclaredSize(file) : 0;
        var entry = new ArchiveEntry(entryPath, false, false, size, File.GetLastWriteTimeUtc(path), false);
        visit(new ArchiveRecord
        {
            Entry = entry,
            OpenData = withData ? () => new PrefixedStream(head.AsSpan(0, count).ToArray(), decompressed) : null,
        });
    }

    private static Stream Decompress(Stream file, ArchiveFormat format) => format switch
    {
        ArchiveFormat.TarGzip or ArchiveFormat.Gzip => new GZipStream(file, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true),
        ArchiveFormat.TarBzip2 or ArchiveFormat.Bzip2 => BZip2Stream.Create(file, CompressionMode.Decompress, true, true, false),
        ArchiveFormat.TarXz or ArchiveFormat.Xz => new XZStream(file),
        _ => file,
    };

    /// <summary>A ustar/GNU/v7 header block: its checksum adds up.</summary>
    internal static bool LooksLikeTar(ReadOnlySpan<byte> block)
    {
        if (block.Length < 512 || block[0] == 0)
        {
            return false;
        }

        var field = Encoding.ASCII.GetString(block.Slice(148, 8)).Trim('\0', ' ');
        if (field.Length == 0)
        {
            return false;
        }

        long declared;
        try
        {
            declared = Convert.ToInt64(field, 8);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }

        long sum = 0;
        for (var i = 0; i < 512; i++)
        {
            sum += i is >= 148 and < 156 ? ' ' : block[i];
        }

        return sum == declared;
    }

    private static long GzipDeclaredSize(FileStream file)
    {
        try
        {
            if (file.Length < 18)
            {
                return 0;
            }

            using var trailer = new FileStream(file.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            trailer.Seek(-4, SeekOrigin.End);
            Span<byte> bytes = stackalloc byte[4];
            trailer.ReadExactly(bytes);
            return BitConverter.ToUInt32(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Replays bytes already read off a forward-only stream, then continues with the rest.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, n).CopyTo(buffer);
                _offset += n;
                return n;
            }

            return rest.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The stream underneath belongs to the walker.
        }
    }
}
