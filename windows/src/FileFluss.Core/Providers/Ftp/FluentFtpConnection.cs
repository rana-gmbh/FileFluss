// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FluentFTP;
using FluentFTP.Exceptions;

namespace FileFluss.Core.Providers.Ftp;

/// <summary>
/// <see cref="IFtpConnection"/> over FluentFTP. macOS drives <c>/usr/bin/curl</c>; a library
/// gives real progress, a persistent control connection and MLSD without parsing curl output.
/// </summary>
internal sealed class FluentFtpConnection : IFtpConnection
{
    private readonly AsyncFtpClient _client;

    private FluentFtpConnection(AsyncFtpClient client)
    {
        _client = client;
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>FluentFTP lists with MLSD whenever the server advertises MLST.</summary>
    public bool ListsInUtc => _client.HasFeature(FtpCapability.MLST);

    public bool CanSetModifiedTime => _client.HasFeature(FtpCapability.MFMT);

    /// <summary>Connects and logs in. Passive mode (EPSV, then PASV), as curl does by default.</summary>
    public static async Task<IFtpConnection> ConnectAsync(FtpCredentials credentials, CancellationToken cancellationToken)
    {
        var config = new FtpConfig
        {
            EncryptionMode = credentials.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,

            // Without this, FluentFTP aborts on any certificate error (no ValidateCertificate
            // handler is installed), which is the strict default the toggle relaxes.
            ValidateAnyCertificate = credentials.UseTls && credentials.AllowInvalidCertificate,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ConnectTimeout = 15_000,
            DataConnectionConnectTimeout = 15_000,
            ReadTimeout = 30_000,
            DataConnectionReadTimeout = 60_000,
        };

        var client = new AsyncFtpClient(credentials.Host, credentials.Username, credentials.Password, credentials.Port, config);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return new FluentFtpConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken)
        => await _client.GetWorkingDirectory(cancellationToken).ConfigureAwait(false);

    public async Task<bool> IsAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client.IsStillConnected(5_000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FtpException or IOException or TimeoutException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var listing = await _client.GetListing(path, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
        return listing.Select(item => new FtpEntry(
            item.Name,
            item.Type switch
            {
                FtpObjectType.Directory => FtpEntryKind.Directory,
                FtpObjectType.Link => FtpEntryKind.Link,
                _ => FtpEntryKind.File,
            },
            item.Size < 0 ? 0 : item.Size,
            item.Modified)).ToList();
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
        => _client.DirectoryExists(path, cancellationToken);

    public async Task DownloadAsync(string path, Stream target, Action<long> onTotal, CancellationToken cancellationToken)
    {
        var ok = await _client.DownloadStream(target, path, 0, new Reporter(onTotal), cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            throw new FtpException($"Download of {path} failed.");
        }
    }

    public async Task UploadAsync(Stream source, string path, Action<long> onTotal, CancellationToken cancellationToken)
    {
        var status = await _client.UploadStream(source, path, FtpRemoteExists.Overwrite, createRemoteDir: false, new Reporter(onTotal), cancellationToken).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
        {
            throw new FtpException($"Upload of {path} failed.");
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        => await _client.CreateDirectory(path, force: false, cancellationToken).ConfigureAwait(false);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        => _client.DeleteFile(path, cancellationToken);

    public async Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        // FluentFTP's DeleteDirectory recurses on its own; the provider walks the tree itself
        // (as macOS does) and only needs the plain RMD.
        var reply = await _client.Execute("RMD " + path, cancellationToken).ConfigureAwait(false);
        if (!reply.Success)
        {
            throw new FtpCommandException(reply);
        }
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken)
        => _client.Rename(path, newPath, cancellationToken);

    public Task SetModifiedTimeAsync(string path, DateTime utc, CancellationToken cancellationToken)
        => _client.SetModifiedTime(path, DateTime.SpecifyKind(utc, DateTimeKind.Utc), cancellationToken);

    public void Dispose() => _client.Dispose();

    /// <summary>Forwards FluentFTP's cumulative byte count synchronously (Progress&lt;T&gt; would post to a context).</summary>
    private sealed class Reporter(Action<long> onTotal) : IProgress<FtpProgress>
    {
        public void Report(FtpProgress value) => onTotal(value.TransferredBytes);
    }
}
