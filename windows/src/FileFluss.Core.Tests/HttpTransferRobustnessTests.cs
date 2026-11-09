// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests;

public sealed class HttpTransferRobustnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-http-" + Guid.NewGuid().ToString("N"));

    public HttpTransferRobustnessTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AStalledBodyFailsInsteadOfHangingAndLeavesNoFile()
    {
        var target = Path.Combine(_root, "stalled.bin");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(100)) };

        var save = HttpTransfer.SaveToFileAsync(response, target, null, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        var finished = await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(save, finished);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => save);
        Assert.Equal(CloudErrorKind.NetworkError, error.Kind);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ABodyShorterThanItsContentLengthIsNotSavedAsComplete()
    {
        var target = Path.Combine(_root, "short.bin");
        var content = new StreamContent(new MemoryStream(new byte[10]));
        content.Headers.ContentLength = 20;
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => HttpTransfer.SaveToFileAsync(response, target, null, CancellationToken.None));

        Assert.Equal(CloudErrorKind.NetworkError, error.Kind);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AFailedDownloadKeepsThePreviousFile()
    {
        var target = Path.Combine(_root, "keep.txt");
        await File.WriteAllTextAsync(target, "previous version");
        var content = new StreamContent(new ThrowingStream(5));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await Assert.ThrowsAnyAsync<Exception>(() => HttpTransfer.SaveToFileAsync(response, target, null, CancellationToken.None));

        Assert.Equal("previous version", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task UserCancelStillSurfacesAsCancellation()
    {
        var target = Path.Combine(_root, "cancel.bin");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(0)) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpTransfer.SaveToFileAsync(response, target, null, cts.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    /// <summary>Yields some bytes, then never answers again (until cancelled) — a half-open connection.</summary>
    private sealed class StallingStream(int bytesFirst) : Stream
    {
        private int _remaining = bytesFirst;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining > 0)
            {
                var n = Math.Min(_remaining, buffer.Length);
                _remaining -= n;
                return n;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Yields some bytes, then fails like a reset connection.</summary>
    private sealed class ThrowingStream(int bytesFirst) : Stream
    {
        private int _remaining = bytesFirst;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining > 0)
            {
                var n = Math.Min(_remaining, buffer.Length);
                _remaining -= n;
                return ValueTask.FromResult(n);
            }

            throw new IOException("connection reset", new System.Net.Sockets.SocketException(10054));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
