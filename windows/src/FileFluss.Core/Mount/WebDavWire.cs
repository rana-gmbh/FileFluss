// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text;

namespace FileFluss.Core.Mount;

/// <summary>A malformed request: answered with its status, then the connection closes.</summary>
internal sealed class HttpProtocolException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The request line and headers of one HTTP/1.1 request.</summary>
internal sealed class DavRequestHead
{
    public required string Method { get; init; }

    /// <summary>The request target exactly as sent (still percent-encoded).</summary>
    public required string RawTarget { get; init; }

    public required string Version { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public string? Header(string name) => Headers.GetValueOrDefault(name);

    public long? ContentLength
    {
        get
        {
            if (Header("Content-Length") is not { } raw)
            {
                return null;
            }

            return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                ? length
                : throw new HttpProtocolException(400, "Bad Content-Length");
        }
    }

    public bool IsChunked
        => Header("Transfer-Encoding") is { } te && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);

    public bool HasBody => IsChunked || ContentLength is > 0;

    /// <summary>HTTP/1.1 keeps the connection unless asked not to; HTTP/1.0 only when asked to.</summary>
    public bool WantsKeepAlive
    {
        get
        {
            var connection = Header("Connection") ?? string.Empty;
            if (connection.Contains("close", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Version != "HTTP/1.0" || connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>One request after its body was read: a small XML body in memory, a PUT body in a temp file.</summary>
internal sealed class DavRequest
{
    public required DavRequestHead Head { get; init; }

    /// <summary>The percent-decoded path, always starting with "/".</summary>
    public required string Path { get; init; }

    public byte[] Body { get; init; } = [];

    /// <summary>The PUT body, streamed to disk; null when the request had no body.</summary>
    public string? BodyFile { get; init; }

    public string Method => Head.Method;

    public string? Header(string name) => Head.Header(name);
}

/// <summary>A response: headers plus nothing, a byte array, or a slice of an open file.</summary>
internal sealed class DavResponse
{
    public DavResponse(int status)
    {
        Status = status;
    }

    public int Status { get; }

    public List<KeyValuePair<string, string>> Headers { get; } = [];

    public byte[]? Body { get; init; }

    public Stream? FileBody { get; init; }

    public long FileOffset { get; init; }

    public long FileLength { get; init; }

    /// <summary>When set, the connection closes after this response (e.g. an unread request body).</summary>
    public bool ForceClose { get; set; }

    public DavResponse With(string name, string value)
    {
        Headers.Add(new(name, value));
        return this;
    }

    public long BodyLength => FileBody is not null ? FileLength : Body?.Length ?? 0;

    public static DavResponse Empty(int status) => new(status);

    public static DavResponse Xml(int status, byte[] xml)
        => new DavResponse(status) { Body = xml }.With("Content-Type", "application/xml; charset=utf-8");

    public static string Reason(int status) => status switch
    {
        100 => "Continue",
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        207 => "Multi-Status",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        412 => "Precondition Failed",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        416 => "Range Not Satisfiable",
        423 => "Locked",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        507 => "Insufficient Storage",
        _ => "Status " + status.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Minimal HTTP/1.1 framing over one TCP connection: request heads, Content-Length and
/// chunked bodies, responses with keep-alive. Written by hand (as the macOS server's is)
/// because <c>HttpListener</c> goes through http.sys, which on a non-admin account only
/// accepts URL prefixes someone registered with <c>netsh http add urlacl</c> — and FileFluss
/// must never need admin rights to mount a drive. A plain <c>TcpListener</c> on 127.0.0.1
/// needs none.
/// </summary>
internal sealed class HttpConnection(Stream stream)
{
    /// <summary>Request line plus headers; the redirector's are well under 4 KB.</summary>
    private const int MaxHeadBytes = 64 * 1024;

    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    /// <summary>Reads the next request head; null when the client closed the connection cleanly between requests.</summary>
    public async Task<DavRequestHead?> ReadHeadAsync(CancellationToken cancellationToken)
    {
        var scanned = 0;
        while (true)
        {
            var available = _end - _start;
            var index = IndexOfHeadEnd(_buffer.AsSpan(_start, available), Math.Max(0, scanned - 3));
            if (index >= 0)
            {
                var text = Encoding.UTF8.GetString(_buffer, _start, index);
                _start += index + 4;
                return ParseHead(text);
            }

            scanned = available;
            if (available >= MaxHeadBytes)
            {
                throw new HttpProtocolException(431, "Request head too large");
            }

            if (await FillAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                if (_end - _start == 0)
                {
                    return null;
                }

                throw new HttpProtocolException(400, "Connection closed inside a request head");
            }
        }
    }

    /// <summary>Copies the request body (Content-Length or chunked) to <paramref name="destination"/>; throws 413 past <paramref name="maxBytes"/>.</summary>
    public async Task ReadBodyAsync(DavRequestHead head, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        if (head.IsChunked)
        {
            await ReadChunkedAsync(destination, maxBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        var remaining = head.ContentLength ?? 0;
        if (remaining > maxBytes)
        {
            throw new HttpProtocolException(413, "Request body too large");
        }

        await CopyExactlyAsync(destination, remaining, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteContinueAsync(CancellationToken cancellationToken)
        => stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).AsTask();

    public async Task WriteResponseAsync(DavResponse response, bool headOnly, bool keepAlive, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(DavResponse.Reason(response.Status)).Append("\r\n");
        head.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");

        // Identify ourselves on every response, as the macOS server does — WebDAV clients
        // fingerprint the Server header.
        head.Append("Server: FileFluss-WebDAV/1.0\r\n");
        head.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        var hasLength = false;
        foreach (var (name, value) in response.Headers)
        {
            hasLength |= name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        // Always frame the body explicitly so a keep-alive client never has to guess.
        if (!hasLength && response.Status is not (< 200 or 204 or 304))
        {
            head.Append("Content-Length: ").Append(response.BodyLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);

        if (!headOnly)
        {
            if (response.FileBody is { } file)
            {
                file.Seek(response.FileOffset, SeekOrigin.Begin);
                var buffer = new byte[256 * 1024];
                var remaining = response.FileLength;
                while (remaining > 0)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        // The file shrank under us: the declared length can't be honoured,
                        // so the connection must not be reused.
                        throw new IOException("Response file ended early");
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            else if (response.Body is { Length: > 0 } body)
            {
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DavRequestHead ParseHead(string text)
    {
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0].Length == 0 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new HttpProtocolException(400, "Malformed request line");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }

        return new DavRequestHead
        {
            Method = requestLine[0].ToUpperInvariant(),
            RawTarget = requestLine[1],
            Version = requestLine[2],
            Headers = headers,
        };
    }

    private static int IndexOfHeadEnd(ReadOnlySpan<byte> span, int from)
    {
        var index = span[from..].IndexOf("\r\n\r\n"u8);
        return index < 0 ? -1 : index + from;
    }

    private async Task<int> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            return 0;
        }

        var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        _end += read;
        return read;
    }

    private async Task CopyExactlyAsync(Stream destination, long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (_end == _start && await FillAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new HttpProtocolException(400, "Connection closed inside a request body");
            }

            var take = (int)Math.Min(count, _end - _start);
            await destination.WriteAsync(_buffer.AsMemory(_start, take), cancellationToken).ConfigureAwait(false);
            _start += take;
            count -= take;
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var index = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8);
            if (index >= 0)
            {
                var line = Encoding.ASCII.GetString(_buffer, _start, index);
                _start += index + 2;
                return line;
            }

            if (_end - _start >= 8 * 1024 || await FillAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new HttpProtocolException(400, "Malformed chunk");
            }
        }
    }

    private async Task ReadChunkedAsync(Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var sizeText = line.Split(';')[0].Trim();
            if (!long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new HttpProtocolException(400, "Malformed chunk size");
            }

            if (size == 0)
            {
                // Trailers, up to the empty line.
                while ((await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Length > 0)
                {
                }

                return;
            }

            total += size;
            if (total > maxBytes)
            {
                throw new HttpProtocolException(413, "Request body too large");
            }

            await CopyExactlyAsync(destination, size, cancellationToken).ConfigureAwait(false);
            if ((await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Length != 0)
            {
                throw new HttpProtocolException(400, "Malformed chunk terminator");
            }
        }
    }
}
