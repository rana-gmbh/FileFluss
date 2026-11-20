// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.WordPress;

/// <summary>
/// A <c>multipart/form-data</c> body with exactly one file part, streamed from disk.
///
/// <para>Written by hand rather than with <see cref="MultipartFormDataContent"/> for two
/// reasons: the part header must carry the file name as raw UTF-8 in
/// <c>filename="…"</c> (PHP ignores RFC 2231 <c>filename*</c> and MIME-encoded words, which is
/// what .NET emits for non-ASCII names, so "Grüße.jpg" would arrive as garbage), and progress
/// must count file bytes only — the macOS body is the same shape.</para>
/// </summary>
internal sealed class MultipartFileContent : HttpContent
{
    private readonly string _localPath;
    private readonly ByteProgress? _onBytes;
    private readonly byte[] _head;
    private readonly byte[] _tail;
    private readonly long _fileLength;

    public MultipartFileContent(string localPath, string fieldName, string fileName, string mimeType, ByteProgress? onBytes)
    {
        _localPath = localPath;
        _onBytes = onBytes;
        _fileLength = new FileInfo(localPath).Length;
        var boundary = Guid.NewGuid().ToString("D").ToUpperInvariant();
        _head = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n" +
            $"Content-Disposition: form-data; name=\"{fieldName}\"; filename=\"{QuoteSafe(fileName)}\"\r\n" +
            $"Content-Type: {mimeType}\r\n\r\n");
        _tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");

        // Unquoted boundary, exactly as macOS sends it; every multipart parser accepts both.
        Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_head, cancellationToken).ConfigureAwait(false);
        await using (var file = HttpTransfer.OpenForUpload(_localPath))
        {
            await HttpTransfer.CopyAsync(file, stream, _onBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.WriteAsync(_tail, cancellationToken).ConfigureAwait(false);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = _head.Length + _fileLength + _tail.Length;
        return true;
    }

    /// <summary>A quote or line break would end the header value early.</summary>
    private static string QuoteSafe(string name)
        => name.Replace("\"", "'", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
}
