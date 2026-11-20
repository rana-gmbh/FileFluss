// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Buffers.Binary;
using System.Text;

namespace FileFluss.Core.VirtualFiles;

/// <summary>
/// The <c>FileGroupDescriptorW</c> clipboard format, byte for byte: a DWORD count followed by
/// one FILEDESCRIPTORW per entry. Built here as plain bytes (no Win32 types) so the layout is
/// unit-testable anywhere; the shell side copies the array into an HGLOBAL as is.
///
/// <code>
/// FILEDESCRIPTORW (592 bytes, 4-byte aligned, no padding)
///   0  DWORD    dwFlags
///   4  CLSID    clsid
///  20  SIZEL    sizel
///  28  POINTL   pointl
///  36  DWORD    dwFileAttributes
///  40  FILETIME ftCreationTime
///  48  FILETIME ftLastAccessTime
///  56  FILETIME ftLastWriteTime
///  64  DWORD    nFileSizeHigh
///  68  DWORD    nFileSizeLow
///  72  WCHAR    cFileName[260]
/// </code>
/// </summary>
public static class FileGroupDescriptor
{
    public const int HeaderSize = 4;
    public const int DescriptorSize = 592;
    public const int NameCapacity = 260;

    public const int OffsetFlags = 0;
    public const int OffsetAttributes = 36;
    public const int OffsetCreationTime = 40;
    public const int OffsetWriteTime = 56;
    public const int OffsetSizeHigh = 64;
    public const int OffsetSizeLow = 68;
    public const int OffsetName = 72;

    // FD_* flags (shlobj_core.h).
    public const uint FdAttributes = 0x00000004;
    public const uint FdCreateTime = 0x00000008;
    public const uint FdWriteTime = 0x00000020;
    public const uint FdFileSize = 0x00000040;
    public const uint FdProgressUi = 0x00004000;
    public const uint FdUnicode = 0x80000000;

    public const uint FileAttributeDirectory = 0x10;
    public const uint FileAttributeNormal = 0x80;

    /// <summary>
    /// The descriptor for <paramref name="entries"/>, in order (lindex N of FileContents is
    /// entry N). <paramref name="progressUi"/> sets FD_PROGRESSUI, asking the receiver to show
    /// its own progress dialog — Explorer's copy dialog then tracks the download.
    /// </summary>
    public static byte[] Build(IReadOnlyList<VirtualFileEntry> entries, bool progressUi = true)
    {
        var bytes = new byte[HeaderSize + (entries.Count * DescriptorSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Write(bytes.AsSpan(HeaderSize + (i * DescriptorSize), DescriptorSize), entries[i], progressUi);
        }

        return bytes;
    }

    private static void Write(Span<byte> target, VirtualFileEntry entry, bool progressUi)
    {
        var name = entry.RelativePath;
        if (name.Length >= NameCapacity)
        {
            throw new VirtualFileTreeException(VirtualFileTreeProblem.PathTooLong);
        }

        var flags = FdAttributes | FdUnicode | (progressUi ? FdProgressUi : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(target[OffsetAttributes..], entry.IsDirectory ? FileAttributeDirectory : FileAttributeNormal);

        if (!entry.IsDirectory && entry.Size is { } size and >= 0)
        {
            flags |= FdFileSize;
            BinaryPrimitives.WriteUInt32LittleEndian(target[OffsetSizeHigh..], (uint)((ulong)size >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(target[OffsetSizeLow..], (uint)((ulong)size & 0xFFFFFFFF));
        }

        if (ToFileTime(entry.Modified) is { } written)
        {
            flags |= FdWriteTime;
            BinaryPrimitives.WriteInt64LittleEndian(target[OffsetWriteTime..], written);
        }

        if (ToFileTime(entry.Created) is { } created)
        {
            flags |= FdCreateTime;
            BinaryPrimitives.WriteInt64LittleEndian(target[OffsetCreationTime..], created);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(target[OffsetFlags..], flags);
        Encoding.Unicode.GetBytes(name, target.Slice(OffsetName, NameCapacity * 2));
    }

    /// <summary>
    /// A FILETIME (100 ns ticks since 1601-01-01 UTC, as one little-endian 64-bit value:
    /// dwLowDateTime then dwHighDateTime), or null for an unknown date or one Windows can't
    /// represent.
    /// </summary>
    public static long? ToFileTime(DateTimeOffset? date)
    {
        if (date is not { } value || value.UtcDateTime.Year < 1601)
        {
            return null;
        }

        return value.ToFileTime();
    }

    /// <summary>One descriptor read back — what a receiver such as Explorer sees.</summary>
    public sealed record Descriptor(uint Flags, uint Attributes, long? Size, DateTimeOffset? Modified, DateTimeOffset? Created, string Name)
    {
        public bool IsDirectory => (Attributes & FileAttributeDirectory) != 0;
    }

    /// <summary>Decodes a FileGroupDescriptorW block; the receiving side of <see cref="Build"/>.</summary>
    public static IReadOnlyList<Descriptor> Parse(ReadOnlySpan<byte> bytes)
    {
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var result = new List<Descriptor>(count);
        for (var i = 0; i < count; i++)
        {
            var d = bytes.Slice(HeaderSize + (i * DescriptorSize), DescriptorSize);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(d[OffsetFlags..]);
            var nameBytes = d.Slice(OffsetName, NameCapacity * 2);
            var name = Encoding.Unicode.GetString(nameBytes);
            var terminator = name.IndexOf('\0');
            result.Add(new Descriptor(
                flags,
                (flags & FdAttributes) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(d[OffsetAttributes..]) : 0,
                (flags & FdFileSize) != 0
                    ? ((long)BinaryPrimitives.ReadUInt32LittleEndian(d[OffsetSizeHigh..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(d[OffsetSizeLow..])
                    : null,
                (flags & FdWriteTime) != 0 ? DateTimeOffset.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(d[OffsetWriteTime..])) : null,
                (flags & FdCreateTime) != 0 ? DateTimeOffset.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(d[OffsetCreationTime..])) : null,
                terminator < 0 ? name : name[..terminator]));
        }

        return result;
    }
}
