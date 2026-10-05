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
    /// Streams a response body to <paramref name="localPath"/>, reporting each chunk. Writes to
    /// a sibling "*.fileflusspart" file and renames on success, so a failed or cancelled
    /// download never leaves a truncated file that looks complete. The response must have been
    /// requested with <see cref="HttpCompletionOption.ResponseHeadersRead"/>.
    /// </summary>
    public static async Task SaveToFileAsync(HttpResponseMessage response, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partial = localPath + ".fileflusspart";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await CopyAsync(source, target, onBytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, localPath, overwrite: true);
        }
        catch (HttpRequestException e)
        {
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
