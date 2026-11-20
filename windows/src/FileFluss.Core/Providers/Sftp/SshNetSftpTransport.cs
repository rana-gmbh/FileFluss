// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace FileFluss.Core.Providers.Sftp;

/// <summary>The private key could not be read (unsupported format, or a wrong/missing passphrase).</summary>
internal sealed class SftpKeyLoadException(bool passphraseProblem, Exception inner) : Exception(inner.Message, inner)
{
    public bool PassphraseProblem { get; } = passphraseProblem;
}

/// <summary>
/// <see cref="ISftpTransport"/> over SSH.NET. macOS shells out to <c>/usr/bin/sftp</c>; a library
/// gives real progress, server-side rename and host-key handling without parsing a TTY.
///
/// <para><b>Symlinks.</b> SSH.NET resolves every path through SSH_FXP_REALPATH before
/// <c>DeleteFile</c>, <c>RenameFile</c>, <c>DeleteDirectory</c> and <c>GetAttributes</c> — and the
/// server's realpath follows symlinks, so deleting or renaming a symlink would delete or rename
/// its <i>target</i>. The OpenSSH <c>sftp</c> client macOS uses sends the path as given. Those
/// operations therefore go to SSH.NET's internal SFTP session directly (bound by reflection;
/// a unit test pins the binding to the referenced SSH.NET version).</para>
/// </summary>
internal sealed class SshNetSftpTransport : ISftpTransport
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private static readonly FieldInfo? SessionField =
        typeof(SftpClient).GetField("_sftpSession", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Type? SessionInterface =
        typeof(SftpClient).Assembly.GetType("Renci.SshNet.Sftp.ISftpSession");

    private static readonly MethodInfo? RemoveMethod =
        SessionInterface?.GetMethod("RequestRemoveAsync", [typeof(string), typeof(CancellationToken)]);

    private static readonly MethodInfo? RemoveDirectoryMethod =
        SessionInterface?.GetMethod("RequestRmDirAsync", [typeof(string), typeof(CancellationToken)]);

    private static readonly MethodInfo? RenameMethod =
        SessionInterface?.GetMethod("RequestRenameAsync", [typeof(string), typeof(string), typeof(CancellationToken)]);

    private static readonly MethodInfo? LStatMethod =
        SessionInterface?.GetMethod("RequestLStatAsync", [typeof(string), typeof(CancellationToken)]);

    private readonly SftpClient _client;
    private readonly ConnectionInfo _connectionInfo;
    private readonly List<IDisposable> _owned;
    private readonly string? _fingerprint;
    private readonly SemaphoreSlim _shellGate = new(1, 1);
    private SshClient? _shell;

    private SshNetSftpTransport(SftpClient client, ConnectionInfo connectionInfo, List<IDisposable> owned, string? fingerprint)
    {
        _client = client;
        _connectionInfo = connectionInfo;
        _owned = owned;
        _fingerprint = fingerprint;
    }

    /// <summary>True when the internal SFTP session calls the symlink-safe operations need are reachable.</summary>
    internal static bool RawSessionAvailable =>
        SessionField is not null && RemoveMethod is not null && RemoveDirectoryMethod is not null &&
        RenameMethod is not null && LStatMethod is not null;

    public bool IsConnected => _client.IsConnected;

    public string WorkingDirectory => _client.WorkingDirectory;

    public static async Task<ISftpTransport> ConnectAsync(SftpCredentials credentials, SftpHostKeyCheck hostKey, CancellationToken cancellationToken)
    {
        var owned = new List<IDisposable>();
        try
        {
            var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Port, credentials.Username, CreateAuthenticationMethods(credentials, owned))
            {
                Timeout = TimeSpan.FromSeconds(15),
                Encoding = Encoding.UTF8,
            };
            var client = new SftpClient(connectionInfo)
            {
                KeepAliveInterval = KeepAlive,
                OperationTimeout = TimeSpan.FromMinutes(2),
            };
            client.HostKeyReceived += (_, e) => e.CanTrust = hostKey.Verify("SHA256:" + e.FingerPrintSHA256);
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            return new SshNetSftpTransport(client, connectionInfo, owned, hostKey.Presented);
        }
        catch
        {
            foreach (var disposable in owned)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Exactly the methods the user chose, so a failure doesn't fall back to another one (macOS
    /// passes PreferredAuthentications for the same reason). Password accounts also answer
    /// keyboard-interactive with the password — many servers only offer that.
    /// </summary>
    private static AuthenticationMethod[] CreateAuthenticationMethods(SftpCredentials credentials, List<IDisposable> owned)
    {
        if (credentials.AuthMethod == SftpAuthMethod.PrivateKey)
        {
            var key = LoadKey(credentials.PrivateKey ?? string.Empty, credentials.Passphrase);
            owned.Add(key);
            var method = new PrivateKeyAuthenticationMethod(credentials.Username, key);
            owned.Add(method);
            return [method];
        }

        var password = credentials.Password ?? string.Empty;
        var passwordMethod = new PasswordAuthenticationMethod(credentials.Username, password);
        var interactive = new KeyboardInteractiveAuthenticationMethod(credentials.Username);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = password;
            }
        };
        owned.Add(passwordMethod);
        owned.Add(interactive);
        return [passwordMethod, interactive];
    }

    private static PrivateKeyFile LoadKey(string keyText, string? passphrase)
    {
        // Some SSH builds reject a key without a final newline.
        var text = keyText.EndsWith('\n') ? keyText : keyText + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        try
        {
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException e)
        {
            throw new SftpKeyLoadException(passphraseProblem: true, e);
        }
        catch (Exception e) when (e is SshException or FormatException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException or NotSupportedException)
        {
            // A wrong passphrase decrypts to garbage, which surfaces as a parse error; with a
            // passphrase given and an encrypted key, that is the likelier cause.
            var encrypted = text.Contains("ENCRYPTED", StringComparison.Ordinal) ||
                            text.Contains("Encryption: aes", StringComparison.OrdinalIgnoreCase) ||
                            e.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
                            e.Message.Contains("decrypt", StringComparison.OrdinalIgnoreCase);
            throw new SftpKeyLoadException(passphraseProblem: encrypted && !string.IsNullOrEmpty(passphrase), e);
        }
    }

    public async Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var entries = new List<SftpEntry>();
        await foreach (var file in _client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ToEntry(file.Name, file.Attributes));
        }

        return entries;
    }

    public async Task<SftpEntry> LStatAsync(string path, CancellationToken cancellationToken)
    {
        var attributes = await InvokeRawAsync<SftpFileAttributes>(LStatMethod, path, cancellationToken).ConfigureAwait(false);
        return ToEntry(NameOf(path), attributes);
    }

    public async Task<SftpEntry> StatAsync(string path, CancellationToken cancellationToken)
    {
        // GetAttributes resolves the path with realpath first, which follows the link.
        var attributes = await _client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
        return ToEntry(NameOf(path), attributes);
    }

    public Task DownloadAsync(string path, Stream target, Action<long> onTotal, CancellationToken cancellationToken)
        => _client.DownloadFileAsync(path, target, new DownloadReporter(onTotal), cancellationToken);

    public Task UploadAsync(Stream source, string path, Action<long> onTotal, CancellationToken cancellationToken)
        => _client.UploadFileAsync(source, path, true, new UploadReporter(onTotal), cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        => _client.CreateDirectoryAsync(path, cancellationToken);

    public Task RemoveAsync(string path, CancellationToken cancellationToken)
        => InvokeRawAsync(RemoveMethod, [path, cancellationToken]);

    public Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken)
        => InvokeRawAsync(RemoveDirectoryMethod, [path, cancellationToken]);

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken)
        => InvokeRawAsync(RenameMethod, [path, newPath, cancellationToken]);

    public Task SetLastWriteTimeUtcAsync(string path, DateTime utc, CancellationToken cancellationToken)
        => Task.Run(() => _client.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(utc, DateTimeKind.Utc)), cancellationToken);

    public async Task<SftpExecResult> ExecuteAsync(string command, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var shell = await ShellAsync(cancellationToken).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } span)
        {
            limit.CancelAfter(span);
        }

        using var ssh = shell.CreateCommand(command);
        try
        {
            await ssh.ExecuteAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SshOperationTimeoutException($"The command did not finish within {timeout}.");
        }

        return new SftpExecResult(ssh.ExitStatus, ssh.Result, ssh.Error);
    }

    /// <summary>
    /// The exec channel's own SSH session (SSH.NET can't share the SFTP one), opened on first
    /// use and pinned to the host key the SFTP session verified.
    /// </summary>
    private async Task<SshClient> ShellAsync(CancellationToken cancellationToken)
    {
        await _shellGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shell is { IsConnected: true } existing)
            {
                return existing;
            }

            _shell?.Dispose();
            _shell = null;
            var shell = new SshClient(_connectionInfo) { KeepAliveInterval = KeepAlive };
            var check = new SftpHostKeyCheck(_fingerprint);
            shell.HostKeyReceived += (_, e) => e.CanTrust = check.Verify("SHA256:" + e.FingerPrintSHA256);
            try
            {
                await shell.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                shell.Dispose();
                throw;
            }

            _shell = shell;
            return shell;
        }
        finally
        {
            _shellGate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _shell?.Dispose();
        _shellGate.Dispose();
        foreach (var disposable in _owned)
        {
            disposable.Dispose();
        }
    }

    private static SftpEntry ToEntry(string name, SftpFileAttributes attributes)
        => new(name, attributes.IsDirectory, attributes.IsSymbolicLink, attributes.Size, attributes.LastWriteTimeUtc);

    private static string NameOf(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private object Session()
    {
        if (!RawSessionAvailable)
        {
            throw CloudProviderException.CommandFailed(Localization.L("This SFTP operation is not supported by the bundled SSH library."));
        }

        return SessionField!.GetValue(_client) ?? throw new SshConnectionException("Client not connected.");
    }

    private async Task InvokeRawAsync(MethodInfo? method, object?[] arguments)
    {
        var session = Session();
        Task task;
        try
        {
            task = (Task)method!.Invoke(session, arguments)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }

        await task.ConfigureAwait(false);
    }

    private async Task<T> InvokeRawAsync<T>(MethodInfo? method, string path, CancellationToken cancellationToken)
    {
        var session = Session();
        Task<T> task;
        try
        {
            task = (Task<T>)method!.Invoke(session, [path, cancellationToken])!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }

        return await task.ConfigureAwait(false);
    }

    private sealed class DownloadReporter(Action<long> onTotal) : IProgress<DownloadFileProgressReport>
    {
        public void Report(DownloadFileProgressReport value) => onTotal(checked((long)value.TotalBytesDownloaded));
    }

    private sealed class UploadReporter(Action<long> onTotal) : IProgress<UploadFileProgressReport>
    {
        public void Report(UploadFileProgressReport value) => onTotal(checked((long)value.TotalBytesUploaded));
    }
}
