// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Providers.Ftp;

/// <summary>Persisted FTP/FTPS account settings, password included (FTP has no tokens).</summary>
internal sealed record FtpCredentials
{
    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; } = 21;

    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    /// <summary>Folder the panel lands in, relative to the login directory; "/" is the login directory itself.</summary>
    [JsonPropertyName("remotePath")]
    public string RemotePath { get; init; } = "/";

    /// <summary>FTPS: explicit AUTH TLS on the control channel, data channel encrypted too.</summary>
    [JsonPropertyName("useTLS")]
    public bool UseTls { get; init; }

    /// <summary>Accept a self-signed or otherwise untrusted certificate (FTPS only).</summary>
    [JsonPropertyName("allowInvalidCertificate")]
    public bool AllowInvalidCertificate { get; init; }
}

internal enum FtpEntryKind
{
    File,
    Directory,
    Link,
}

/// <summary>One listing row as the library parsed it (MLSD, or LIST through FluentFTP's parsers).</summary>
/// <param name="Modified">As the server reported it: UTC for MLSD, the server's local time for LIST; MinValue when unknown.</param>
internal sealed record FtpEntry(string Name, FtpEntryKind Kind, long Size, DateTime Modified);

/// <summary>
/// One logged-in control connection. FTP does one thing at a time per connection, so the
/// provider leases connections from a <see cref="FtpConnectionPool"/> — a long upload must not
/// block the panel's next listing (macOS ran a separate curl per command for the same reason).
/// Implemented by <see cref="FluentFtpConnection"/>; tests script a fake.
/// </summary>
internal interface IFtpConnection : IDisposable
{
    bool IsConnected { get; }

    /// <summary>True when the server lists with MLSD, whose timestamps are UTC.</summary>
    bool ListsInUtc { get; }

    /// <summary>True when the server accepts MFMT.</summary>
    bool CanSetModifiedTime { get; }

    /// <summary>PWD right after login: the login directory the account is rooted at.</summary>
    Task<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken);

    /// <summary>NOOP round trip, to check an idle connection before reuse.</summary>
    Task<bool> IsAliveAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>CWD test. Some servers answer LIST of a missing folder with an empty listing, so a listing alone proves nothing.</summary>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken);

    /// <param name="onTotal">Cumulative bytes transferred so far.</param>
    Task DownloadAsync(string path, Stream target, Action<long> onTotal, CancellationToken cancellationToken);

    /// <param name="onTotal">Cumulative bytes transferred so far.</param>
    Task UploadAsync(Stream source, string path, Action<long> onTotal, CancellationToken cancellationToken);

    /// <summary>MKD; quietly succeeds when the folder exists.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>RMD — the folder must be empty.</summary>
    Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>RNFR + RNTO.</summary>
    Task RenameAsync(string path, string newPath, CancellationToken cancellationToken);

    Task SetModifiedTimeAsync(string path, DateTime utc, CancellationToken cancellationToken);
}
