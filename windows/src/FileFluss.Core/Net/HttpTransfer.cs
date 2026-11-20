// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Net;

/// <summary>Streaming download/upload helpers with byte-level progress.</summary>
public static class HttpTransfer
{
    public const int BufferSize = 128 * 1024;

    /// <summary>
    /// How long a download may go without receiving a single byte before it is abandoned.
    /// The HttpClient has no overall timeout (a 7 GB file takes hours), so without this a
    /// half-open connection — Wi-Fi switched, a NAT entry dropped — waits for ever.
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Streams a response body to <paramref name="localPath"/>, reporting each chunk. Writes to
    /// a sibling "*.fileflusspart" file and renames on success, so a failed or cancelled
    /// download never leaves a truncated file that looks complete. A body shorter than its
    /// Content-Length, or one that stalls for <see cref="StallTimeout"/>, is a network error.
    /// The response must have been requested with <see cref="HttpCompletionOption.ResponseHeadersRead"/>.
    /// </summary>
    public static Task SaveToFileAsync(HttpResponseMessage response, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
        => SaveToFileAsync(response, localPath, onBytes, StallTimeout, cancellationToken);

    internal static async Task SaveToFileAsync(HttpResponseMessage response, string localPath, ByteProgress? onBytes, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partial = localPath + ".fileflusspart";
        try
        {
            long written;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                written = await CopyWatchingForStallsAsync(source, target, onBytes, stallTimeout, cancellationToken).ConfigureAwait(false);
            }

            // Content-Length survives only when the body wasn't decompressed on the way in, so
            // where it is present it is exactly the byte count the file must have.
            if (response.Content.Headers.ContentLength is { } expected && expected != written)
            {
                throw CloudProviderException.Network(new IOException(
                    Localization.L("The download ended after {0} of {1} bytes.", written, expected)));
            }

            File.Move(partial, localPath, overwrite: true);
        }
        catch (HttpRequestException e)
        {
            TryDelete(partial);
            throw CloudProviderException.Network(e);
        }
        catch (HttpIOException e)
        {
            // "The response ended prematurely": the connection closed mid-body.
            TryDelete(partial);
            throw CloudProviderException.Network(e);
        }
        catch (ObjectDisposedException e)
        {
            // The provider was disposed (account re-authenticated or edited) mid-download.
            TryDelete(partial);
            throw CloudProviderException.Network(e);
        }
        catch (IOException e) when (e.InnerException is not null && e is not FileNotFoundException)
        {
            // A connection reset mid-body surfaces as an IOException wrapping a socket error.
            TryDelete(partial);
            throw CloudProviderException.Network(e);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// <see cref="CopyAsync"/> for a network source: each read must produce something within
    /// <paramref name="stallTimeout"/>, or the copy fails as a timeout. Returns the bytes copied.
    /// </summary>
    public static async Task<long> CopyWatchingForStallsAsync(Stream source, Stream target, ByteProgress? onBytes, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            idle.CancelAfter(stallTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw CloudProviderException.Network(new TimeoutException(Localization.L("The request timed out.")));
            }

            if (read == 0)
            {
                return total;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            onBytes?.Invoke(read);
        }
    }

    /// <summary>Copies a stream in chunks, reporting each chunk as a delta.</summary>
    public static async Task CopyAsync(Stream source, Stream target, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onBytes?.Invoke(read);
        }
    }

    /// <summary>Opens a local file for upload with sharing that tolerates other readers.</summary>
    public static FileStream OpenForUpload(string localPath)
        => new(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Request content that streams from a <see cref="Stream"/> (usually a file, or a slice of
/// one for chunked uploads) and reports bytes as they are handed to the network. Sets
/// Content-Length, which most upload endpoints require.
/// </summary>
public sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _length;
    private readonly ByteProgress? _onBytes;
    private readonly bool _leaveOpen;

    /// <param name="length">Bytes to send; defaults to the remainder of a seekable stream.</param>
    public ProgressStreamContent(Stream source, ByteProgress? onBytes, long? length = null, string? contentType = "application/octet-stream", bool leaveOpen = false)
    {
        _source = source;
        _onBytes = onBytes;
        _leaveOpen = leaveOpen;
        _length = length ?? (source.CanSeek ? source.Length - source.Position : -1);
        if (contentType is not null)
        {
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
    }

    /// <summary>Content for a whole local file.</summary>
    public static ProgressStreamContent FromFile(string localPath, ByteProgress? onBytes, string? contentType = "application/octet-stream")
        => new(HttpTransfer.OpenForUpload(localPath), onBytes, contentType: contentType);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = new byte[HttpTransfer.BufferSize];
        var remaining = _length < 0 ? long.MaxValue : _length;
        while (remaining > 0)
        {
            var read = await _source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
            _onBytes?.Invoke(read);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return _length >= 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A read-only window onto part of a file, for chunked/multipart uploads.</summary>
public sealed class FileSliceStream : Stream
{
    private readonly FileStream _file;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public FileSliceStream(string path, long start, long length)
    {
        _file = HttpTransfer.OpenForUpload(path);
        _start = start;
        _length = Math.Max(0, Math.Min(length, _file.Length - start));
        _file.Position = start;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            _position = Math.Clamp(value, 0, _length);
            _file.Position = _start + _position;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = (int)Math.Min(count, _length - _position);
        if (allowed <= 0)
        {
            return 0;
        }

        var read = _file.Read(buffer, offset, allowed);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _position);
        if (allowed <= 0)
        {
            return 0;
        }

        var read = await _file.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}
