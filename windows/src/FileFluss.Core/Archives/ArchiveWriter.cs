// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Providers;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Writers.SevenZip;
using CompressionMode = SharpCompress.Compressors.CompressionMode;

namespace FileFluss.Core.Archives;

/// <summary>
/// Makes archives (the writing half of macOS <c>ArchiveService</c>).
///
/// <para>macOS shells out to <c>/usr/bin/zip</c> and <c>/usr/bin/bsdtar</c>. Windows ships a
/// bsdtar as well, but its build crashes with an access violation on any file name outside
/// the ANSI code page (a file called 日本語.txt is enough), so ZIP, 7-Zip, TAR.GZ and TAR.BZ2
/// are written in-process: ZIP by .NET, the tarballs by .NET's <see cref="TarWriter"/> (PAX,
/// so every name is stored as UTF-8), 7-Zip by SharpCompress. Nothing in-process can write XZ,
/// so TAR.XZ is the same in-process tarball, written under a plain ASCII name and then
/// compressed by Windows' tar.exe in its "raw" mode — which never sees a user's file name.
/// RAR needs RARLAB's own tool (<see cref="RarTool"/>).</para>
///
/// <para>Every archive is written under a temporary name and renamed when complete, so a
/// failure or a cancel leaves nothing half-written beside the originals.</para>
/// </summary>
public static class ArchiveWriter
{
    /// <summary>Windows' own bsdtar, used only to XZ-compress a finished tarball.</summary>
    public static string SystemTar => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

    /// <summary>Whether <paramref name="format"/> can be written on this system right now.</summary>
    public static bool CanCreate(ArchiveCreationFormat format) => format switch
    {
        ArchiveCreationFormat.TarXz => File.Exists(SystemTar),
        ArchiveCreationFormat.Rar => RarTool.Find() is not null,
        _ => true,
    };

    /// <summary>
    /// Packs <paramref name="names"/> (plain names of items in <paramref name="directory"/>)
    /// into a new archive in the same folder and returns its path.
    /// <paramref name="onFileAdded"/> hears each item's archive path as it goes in;
    /// <paramref name="onBytes"/> the bytes read from the originals.
    /// </summary>
    public static Task<string> CreateAsync(
        string directory,
        IReadOnlyList<string> names,
        string archiveName,
        ArchiveCreationFormat format,
        Action<string>? onFileAdded = null,
        ByteProgress? onBytes = null,
        CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            var archivePath = Path.Combine(directory, archiveName);
            if (archiveName != Path.GetFileName(archiveName) || ArchiveEntry.Normalise(archiveName) != archiveName)
            {
                throw ArchiveException.UnsafeEntry(archiveName);
            }

            // Appending to an archive that already exists would silently merge this selection
            // into an older file.
            if (File.Exists(archivePath) || Directory.Exists(archivePath))
            {
                throw ArchiveException.AlreadyExists(archiveName);
            }

            foreach (var name in names)
            {
                if (name != Path.GetFileName(name) || name is "." or "..")
                {
                    throw ArchiveException.UnsafeEntry(name);
                }
            }

            var partial = Path.Combine(directory, $".{archiveName}.{Guid.NewGuid():N}.partial.{format.FileExtension()}");
            try
            {
                switch (format)
                {
                    case ArchiveCreationFormat.Rar:
                        await RunRarAsync(directory, names, Path.GetFileName(partial), onFileAdded, cancellationToken).ConfigureAwait(false);
                        break;
                    case ArchiveCreationFormat.TarXz:
                        await WriteTarXzAsync(directory, names, partial, onFileAdded, onBytes, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        using (var output = CreateHidden(partial))
                        {
                            WriteManaged(output, directory, names, format, onFileAdded, onBytes, cancellationToken);
                        }

                        break;
                }

                new FileInfo(partial).Attributes &= ~FileAttributes.Hidden;
                try
                {
                    File.Move(partial, archivePath, overwrite: false);
                }
                catch (IOException) when (File.Exists(archivePath))
                {
                    throw ArchiveException.AlreadyExists(archiveName);
                }

                return archivePath;
            }
            finally
            {
                TryDelete(partial);
            }
        }, cancellationToken);

    private static FileStream CreateHidden(string path)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, ArchiveReader.BlockSize);
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return stream;
    }

    // MARK: - What goes in

    /// <summary>One item to pack: where it is on disk and what it is called inside the archive.</summary>
    internal sealed record PackItem(string SourcePath, string EntryName, bool IsDirectory, DateTime ModifiedUtc);

    /// <summary>
    /// The selection, folders expanded, parents before children. Links (symbolic links and
    /// junctions) are left out rather than followed: following one can leave the folder, or
    /// loop. Cloud placeholders (OneDrive's files on demand) are reparse points too, but no
    /// links, so they go in — reading one downloads it, as any app reading it would.
    /// </summary>
    internal static IEnumerable<PackItem> Expand(string directory, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                throw CloudProviderException.NotFound(name);
            }

            foreach (var item in ExpandOne(info, name, cancellationToken))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<PackItem> ExpandOne(FileSystemInfo root, string rootName, CancellationToken cancellationToken)
    {
        var pending = new Stack<(FileSystemInfo Info, string Name)>();
        pending.Push((root, rootName));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (info, name) = pending.Pop();
            if (info.LinkTarget is not null)
            {
                SupportLogger.Shared.Log($"compress: left out link \"{name}\"", "archive");
                continue;
            }

            if (info is DirectoryInfo folder)
            {
                yield return new PackItem(folder.FullName, name, true, folder.LastWriteTimeUtc);
                var children = folder.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 })
                    .OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var child in children)
                {
                    pending.Push((child, name + "/" + child.Name));
                }
            }
            else
            {
                yield return new PackItem(info.FullName, name, false, info.LastWriteTimeUtc);
            }
        }
    }

    // MARK: - In-process formats

    private static void WriteManaged(Stream output, string directory, IReadOnlyList<string> names, ArchiveCreationFormat format, Action<string>? onFileAdded, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var items = Expand(directory, names, cancellationToken);
        switch (format)
        {
            case ArchiveCreationFormat.Zip:
                WriteZip(output, items, onFileAdded, onBytes, cancellationToken);
                break;
            case ArchiveCreationFormat.SevenZip:
                Write7z(output, items, onFileAdded, onBytes, cancellationToken);
                break;
            case ArchiveCreationFormat.TarGzip:
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    WriteTar(gzip, items, onFileAdded, onBytes, cancellationToken);
                }

                break;
            case ArchiveCreationFormat.TarBzip2:
                using (var bzip2 = BZip2Stream.Create(output, CompressionMode.Compress, false, true, false))
                {
                    WriteTar(bzip2, items, onFileAdded, onBytes, cancellationToken);
                    bzip2.Finish();
                }

                break;
            default:
                throw new NotSupportedException(format.ToString());
        }
    }

    private static void WriteZip(Stream output, IEnumerable<PackItem> items, Action<string>? onFileAdded, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        // Names go in as UTF-8 with the flag that says so, which every current unzip reads.
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(item.IsDirectory ? item.EntryName + "/" : item.EntryName, CompressionLevel.Optimal);
            entry.LastWriteTime = ZipDate(item.ModifiedUtc);
            if (!item.IsDirectory)
            {
                using var source = OpenSource(item.SourcePath, onBytes, cancellationToken);
                using var target = entry.Open();
                source.CopyTo(target, ArchiveReader.BlockSize);
            }

            onFileAdded?.Invoke(item.EntryName);
        }
    }

    /// <summary>Zip stores DOS dates: 1980–2107, local time. Anything outside is clamped rather than refused.</summary>
    private static DateTimeOffset ZipDate(DateTime utc)
    {
        var local = utc.ToLocalTime();
        if (local.Year < 1980)
        {
            local = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }
        else if (local.Year > 2107)
        {
            local = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        }

        return new DateTimeOffset(local);
    }

    private static void Write7z(Stream output, IEnumerable<PackItem> items, Action<string>? onFileAdded, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        using var writer = new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA) { LeaveStreamOpen = true });
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                writer.WriteDirectory(item.EntryName, item.ModifiedUtc);
            }
            else
            {
                using var source = OpenSource(item.SourcePath, onBytes, cancellationToken);
                writer.Write(item.EntryName, source, item.ModifiedUtc);
            }

            onFileAdded?.Invoke(item.EntryName);
        }
    }

    private static void WriteTar(Stream output, IEnumerable<PackItem> items, Action<string>? onFileAdded, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        // PAX: long names, UTF-8 names and sizes over 8 GB, read by every tar from the last
        // twenty years.
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.EntryName + "/")
                {
                    ModificationTime = item.ModifiedUtc,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                });
            }
            else
            {
                using var source = OpenSource(item.SourcePath, onBytes, cancellationToken);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
                {
                    ModificationTime = item.ModifiedUtc,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    DataStream = source,
                });
            }

            onFileAdded?.Invoke(item.EntryName);
        }
    }

    private static Stream OpenSource(string path, ByteProgress? onBytes, CancellationToken cancellationToken)
        => new ProgressReadStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ArchiveReader.BlockSize, FileOptions.SequentialScan), onBytes, cancellationToken);

    // MARK: - TAR.XZ

    private static async Task WriteTarXzAsync(string directory, IReadOnlyList<string> names, string partial, Action<string>? onFileAdded, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        if (!File.Exists(SystemTar))
        {
            throw ArchiveException.ToolMissing("tar");
        }

        // The working folder sits beside the result, so the final move is a rename.
        var work = Directory.CreateDirectory(Path.Combine(directory, $".FileFluss-compress-{Guid.NewGuid():N}"));
        try
        {
            work.Attributes |= FileAttributes.Hidden;
            using (var tar = new FileStream(Path.Combine(work.FullName, "data.tar"), FileMode.CreateNew, FileAccess.Write, FileShare.None, ArchiveReader.BlockSize))
            {
                WriteTar(tar, Expand(directory, names, cancellationToken), onFileAdded, onBytes, cancellationToken);
            }

            // "raw" writes the one input file's bytes with no archive around them: tar.exe
            // only ever sees "data.tar" and "data.tar.xz".
            await RunToolAsync(SystemTar, ["-c", "--format", "raw", "-J", "-f", "data.tar.xz", "data.tar"], work.FullName, null, cancellationToken).ConfigureAwait(false);
            File.Move(Path.Combine(work.FullName, "data.tar.xz"), partial);
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // MARK: - RAR

    /// <summary>
    /// A name inside the working directory, written so that a command-line tool can only read
    /// it as a path: a file can be called anything, including "-df" (rar's "delete files after
    /// archiving") or "@list" (a list file). With ".\" in front, no name is an option.
    /// </summary>
    public static string CommandLinePath(string name) => ".\\" + name;

    private static async Task RunRarAsync(string directory, IReadOnlyList<string> names, string archiveName, Action<string>? onFileAdded, CancellationToken cancellationToken)
    {
        var tool = RarTool.Find() ?? throw ArchiveException.ToolMissing("rar");

        // -r recurses, -ol stores links as links, -y answers any prompt with yes so the tool
        // never waits on a console that isn't there; "--" ends the switches.
        var arguments = new List<string> { "a", "-r", "-ol", "-y", "--", CommandLinePath(archiveName) };
        arguments.AddRange(names.Select(CommandLinePath));
        await RunToolAsync(tool, arguments, directory, line =>
        {
            if (AddedFileName(line) is { } added)
            {
                onFileAdded?.Invoke(added);
            }
            else
            {
                SupportLogger.Shared.Log($"rar: {line}", "archive");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The file name out of a tool's progress line, or null for anything else it says. rar
    /// prints "Adding    sub\file.txt    OK", bsdtar "a sub/file.txt" (macOS also parses
    /// zip's "  adding: sub/file.txt (deflated 62%)").
    /// </summary>
    public static string? AddedFileName(string line)
    {
        var trimmed = line.Trim();
        foreach (var prefix in new[] { "adding: ", "updating: " })
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                var name = trimmed[prefix.Length..];
                var paren = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (paren >= 0)
                {
                    name = name[..paren];
                }

                return name.Length == 0 ? null : name;
            }
        }

        if (trimmed.StartsWith("a ", StringComparison.Ordinal) && trimmed.Length > 2)
        {
            return trimmed[2..];
        }

        if (trimmed.StartsWith("Adding ", StringComparison.Ordinal))
        {
            var name = trimmed["Adding ".Length..].Trim();

            // rar pads the name and appends a percentage and "OK".
            if (name.EndsWith("OK", StringComparison.Ordinal))
            {
                name = name[..^2];
            }

            name = name.TrimEnd().TrimEnd('%', '\b').TrimEnd();
            while (name.Length > 0 && (char.IsDigit(name[^1]) || name[^1] is '%' or '\b'))
            {
                name = name[..^1];
            }

            name = name.Trim();
            if (name.StartsWith(".\\", StringComparison.Ordinal) || name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            return name.Length == 0 ? null : name;
        }

        return null;
    }

    /// <summary>
    /// Runs a helper tool and waits for it. Output is drained as it comes (a full pipe would
    /// block the tool for good); a cancel kills the process tree.
    /// </summary>
    private static async Task RunToolAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        var collected = new StringBuilder();
        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            lock (collected)
            {
                collected.AppendLine(e.Data);
                if (collected.Length > 8192)
                {
                    collected.Remove(0, collected.Length - 4096);
                }
            }

            onLine?.Invoke(e.Data);
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw ArchiveException.ToolFailed(Path.GetFileNameWithoutExtension(executable), e.NativeErrorCode, e.Message);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            string output;
            lock (collected)
            {
                output = collected.ToString();
            }

            throw ArchiveException.ToolFailed(Path.GetFileNameWithoutExtension(executable), process.ExitCode, output.Length > 2048 ? output[^2048..] : output);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reports bytes as they are read from an original, and notices a cancel between blocks.</summary>
    private sealed class ProgressReadStream(Stream inner, ByteProgress? onBytes, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer);
            if (read > 0)
            {
                onBytes?.Invoke(read);
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
