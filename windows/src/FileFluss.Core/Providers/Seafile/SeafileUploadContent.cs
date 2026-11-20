// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.Seafile;

/// <summary>
/// The multipart/form-data body Seafile's upload link expects — text fields first, then the
/// file — streamed from disk with byte progress, never buffered.
///
/// <para>Written by hand rather than with <see cref="MultipartFormDataContent"/>: that encodes a
/// non-ASCII file name as RFC 2047/5987 (<c>filename*=utf-8''…</c>), which Seafile's file
/// server does not decode, so "Übersicht.pdf" would arrive under a mangled name. The macOS
/// app sends the name as raw UTF-8 inside the quotes, and so does this.</para>
/// </summary>
internal sealed class SeafileUploadContent : HttpContent
{
    private readonly byte[] _preamble;
    private readonly byte[] _epilogue;
    private readonly string _localPath;
    private readonly long _fileLength;
    private readonly ByteProgress? _onBytes;

    public SeafileUploadContent(string localPath, string fileName, IReadOnlyList<(string Name, string Value)> fields, ByteProgress? onBytes)
    {
        _localPath = localPath;
        _onBytes = onBytes;
        _fileLength = new FileInfo(localPath).Length;

        var boundary = "Boundary-" + Guid.NewGuid().ToString("N");
        var head = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            head.Append("--").Append(boundary).Append("\r\n")
                .Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n")
                .Append(value).Append("\r\n");
        }

        head.Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Disposition: form-data; name=\"file\"; filename=\"").Append(QuotedName(fileName)).Append("\"\r\n")
            .Append("Content-Type: application/octet-stream\r\n\r\n");
        _preamble = Encoding.UTF8.GetBytes(head.ToString());
        _epilogue = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

        var contentType = new MediaTypeHeaderValue("multipart/form-data");
        contentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
        Headers.ContentType = contentType;
    }

    /// <summary>The bytes before the file data, for tests.</summary>
    internal string PreambleText => Encoding.UTF8.GetString(_preamble);

    /// <summary>A quote or a line break would end the header value early; escape them the way browsers do.</summary>
    private static string QuotedName(string name)
        => name.Replace("\"", "%22", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_preamble, cancellationToken).ConfigureAwait(false);
        await using (var file = HttpTransfer.OpenForUpload(_localPath))
        {
            var buffer = new byte[HttpTransfer.BufferSize];
            var remaining = _fileLength;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // The file shrank under us; the declared length can no longer be honoured.
                    throw new IOException(Localization.L("The file changed while it was being uploaded."));
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                _onBytes?.Invoke(read);
            }
        }

        await stream.WriteAsync(_epilogue, cancellationToken).ConfigureAwait(false);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = _preamble.Length + _fileLength + _epilogue.Length;
        return true;
    }
}
