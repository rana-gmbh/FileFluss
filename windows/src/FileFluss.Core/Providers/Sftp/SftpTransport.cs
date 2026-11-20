// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Providers.Sftp;

[JsonConverter(typeof(JsonStringEnumConverter<SftpAuthMethod>))]
internal enum SftpAuthMethod
{
    [JsonStringEnumMemberName("password")] Password,
    [JsonStringEnumMemberName("privateKey")] PrivateKey,
}

/// <summary>
/// Persisted SFTP account settings. Field names follow the macOS keychain record; older
/// records without the newer fields decode with their defaults.
/// </summary>
internal sealed record SftpCredentials
{
    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; } = 22;

    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("authMethod")]
    public SftpAuthMethod AuthMethod { get; init; } = SftpAuthMethod.Password;

    /// <summary>Set for password auth.</summary>
    [JsonPropertyName("password")]
    public string? Password { get; init; }

    /// <summary>The private key's text (OpenSSH, PEM or PuTTY), read once from the chosen file.</summary>
    [JsonPropertyName("privateKey")]
    public string? PrivateKey { get; init; }

    /// <summary>Null when the key is not encrypted.</summary>
    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; init; }

    /// <summary>The folder the panel lands in; "/" by default.</summary>
    [JsonPropertyName("remotePath")]
    public string RemotePath { get; init; } = "/";

    /// <summary>
    /// "SHA256:…" of the server's host key, pinned on first connect (OpenSSH's
    /// <c>StrictHostKeyChecking=accept-new</c>, which macOS uses). Null until then.
    /// </summary>
    [JsonPropertyName("hostKeyFingerprint")]
    public string? HostKeyFingerprint { get; init; }
}

/// <summary>
/// One directory entry or stat result.
/// </summary>
/// <param name="IsDirectory">For a symlink as listed (lstat): false; after <see cref="ISftpTransport.StatAsync"/>: the target's.</param>
/// <param name="LastWriteUtc">MinValue when the server sent no time.</param>
internal sealed record SftpEntry(string Name, bool IsDirectory, bool IsSymlink, long Size, DateTime LastWriteUtc);

/// <summary>Result of a command on an SSH exec channel.</summary>
internal sealed record SftpExecResult(int? ExitStatus, string Output, string Error);

/// <summary>
/// Host-key verification for one connection attempt: trust on first use (nothing expected →
/// accept and remember), and refuse a key that differs from the pinned one.
/// </summary>
internal sealed class SftpHostKeyCheck(string? expected)
{
    public string? Expected { get; } = expected;

    /// <summary>What the server presented, "SHA256:…".</summary>
    public string? Presented { get; private set; }

    /// <summary>The server presented a key other than the pinned one; the connection was refused.</summary>
    public bool Mismatch { get; private set; }

    /// <summary>Called during key exchange; returns whether to trust the key.</summary>
    public bool Verify(string fingerprint)
    {
        Presented = fingerprint;
        if (Expected is null || string.Equals(Expected, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        Mismatch = true;
        return false;
    }
}

/// <summary>
/// The SFTP operations the provider needs, as the server sees them: absolute POSIX paths, no
/// FileFluss conventions. Implemented over SSH.NET by <see cref="SshNetSftpTransport"/>; tests
/// script a fake. One connected session; SFTP multiplexes requests, so concurrent calls are fine.
/// </summary>
internal interface ISftpTransport : IDisposable
{
    bool IsConnected { get; }

    /// <summary>The absolute directory the session started in (the login home).</summary>
    string WorkingDirectory { get; }

    /// <summary>Entries as the server lists them (symlinks not followed). "." and ".." may be included.</summary>
    Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>Attributes of <paramref name="path"/> itself (lstat): a symlink reports as a symlink.</summary>
    Task<SftpEntry> LStatAsync(string path, CancellationToken cancellationToken);

    /// <summary>Attributes with symlinks followed; a dangling link reports the link itself.</summary>
    Task<SftpEntry> StatAsync(string path, CancellationToken cancellationToken);

    /// <param name="onTotal">Cumulative bytes so far.</param>
    Task DownloadAsync(string path, Stream target, Action<long> onTotal, CancellationToken cancellationToken);

    /// <summary>Creates or replaces the remote file.</summary>
    /// <param name="onTotal">Cumulative bytes so far.</param>
    Task UploadAsync(Stream source, string path, Action<long> onTotal, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>SSH_FXP_REMOVE on the path itself — removes a symlink, never its target.</summary>
    Task RemoveAsync(string path, CancellationToken cancellationToken);

    /// <summary>SSH_FXP_RMDIR; the folder must be empty.</summary>
    Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>SSH_FXP_RENAME of the path itself; fails when the target exists.</summary>
    Task RenameAsync(string path, string newPath, CancellationToken cancellationToken);

    Task SetLastWriteTimeUtcAsync(string path, DateTime utc, CancellationToken cancellationToken);

    /// <summary>Runs a command on an SSH exec channel (a second session, opened on first use).</summary>
    Task<SftpExecResult> ExecuteAsync(string command, TimeSpan? timeout, CancellationToken cancellationToken);
}

/// <summary>Opens and authenticates a session; the host key goes through <paramref name="hostKey"/>.</summary>
internal delegate Task<ISftpTransport> SftpTransportFactory(SftpCredentials credentials, SftpHostKeyCheck hostKey, CancellationToken cancellationToken);
