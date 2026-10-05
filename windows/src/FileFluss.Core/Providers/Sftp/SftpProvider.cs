// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Sftp;

/// <summary>
/// SFTP over SSH, with password or private-key login. macOS drives <c>/usr/bin/sftp</c> in batch
/// mode over a ControlMaster connection; this keeps one SSH.NET session (SFTP multiplexes
/// requests, so panels, transfers and the indexer share it) with keep-alives, and reconnects
/// when it drops.
///
/// <para><b>Host keys.</b> Like OpenSSH's <c>StrictHostKeyChecking=accept-new</c> on macOS: the
/// first key seen is pinned with the credentials, and a different key later is a hard failure.
/// Editing the account asks whether to trust a changed key (macOS made the user edit
/// known_hosts).</para>
///
/// <para><b>Listings</b> come from SFTP attributes rather than <c>ls -la</c> text, which ends the
/// "empty folder" class of bugs (issues #31/#42: unparsed long-name formats, a silently ignored
/// <c>ls -L</c>). Symlinks are followed for display — a link to a folder opens like a folder,
/// as the original <c>-L</c> intended — but never for delete, recursion or size, so a link
/// loop can't run forever and deleting a link never touches its target. A listing that fails
/// (permission denied) is an error, never an empty folder.</para>
///
/// <para><b>Server-side copy</b> (issue #62): SFTP has none, so it runs <c>cp -Rp</c> on an SSH
/// exec channel when the account has a shell; SFTP-only accounts get NotImplemented and the
/// caller copies through the client.</para>
/// </summary>
[CloudProvider(CloudProviderType.Sftp)]
public sealed class SftpProvider : CloudProvider
{
    private const string LogCategory = "sftp";

    /// <summary>Symlink targets are looked up at most this many at once.</summary>
    private const int LinkResolveConcurrency = 8;

    private static readonly TimeSpan ShellProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly SftpTransportFactory _factory;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private SftpCredentials? _credentials;
    private ISftpTransport? _transport;

    /// <summary>Whether the account may run <c>cp</c>: 0 unknown, 1 yes, 2 no. Per session of this provider.</summary>
    private int _shellCopy;

    public SftpProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    /// <summary>Test seam: tests pass a factory of scripted transports; null → SSH.NET.</summary>
    internal SftpProvider(Guid accountId, ICredentialStore credentials, SftpTransportFactory? factory)
        : base(accountId, credentials)
    {
        _factory = factory ?? SshNetSftpTransport.ConnectAsync;
        var stored = Credentials.Load<SftpCredentials>(CredentialKey);
        if (stored is { Host.Length: > 0, Username.Length: > 0 })
        {
            _credentials = stored;
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Sftp;

    protected override string CredentialKeyPrefix => "sftp";

    public override bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _credentials is not null;
            }
        }
    }

    public override ConnectSpec ConnectSpec
    {
        get
        {
            ConnectField[] common =
            [
                ConnectField.Info("Enter your SFTP server details. You can authenticate with either a password or a private SSH key, and pin the panel to a specific remote folder on first open."),
                new ConnectField("host", ConnectFieldKind.Text, "Hostname") { Placeholder = "Hostname (e.g. server.example.com)" },
                new ConnectField("port", ConnectFieldKind.Number, "Port") { DefaultValue = "22" },
                new ConnectField("username", ConnectFieldKind.Text, "Username"),
                new ConnectField("remotePath", ConnectFieldKind.Text, "Remote Path") { Placeholder = "Remote Path (e.g. /home/me or leave as /)", DefaultValue = "/", Required = false },
            ];

            return new ConnectSpec(
            [
                new ConnectMode("password", "Password", [.. common, new ConnectField("password", ConnectFieldKind.Password, "Password")]),
                new ConnectMode("privateKey", "SSH Key",
                [
                    .. common,
                    new ConnectField("privateKey", ConnectFieldKind.FileContents, "Choose Private Key…")
                    {
                        FileFilter = "Key files|id_*;*.pem;*.key;*.ppk|All files|*.*",
                    },
                    new ConnectField("passphrase", ConnectFieldKind.Password, "Passphrase (leave empty if key has none)") { Required = false },
                    ConnectField.Info("FileFluss reads the key file once and stores its contents in the macOS Keychain — moving or deleting the original file later won't affect this account."),
                ]),
            ]);
        }
    }

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
    {
        lock (_gate)
        {
            if (_credentials is not { } c)
            {
                return new Dictionary<string, string>();
            }

            return new Dictionary<string, string>
            {
                ["host"] = c.Host,
                ["port"] = c.Port.ToString(CultureInfo.InvariantCulture),
                ["username"] = c.Username,
                ["remotePath"] = c.RemotePath,
                [ConnectSpec.ModeKey] = c.AuthMethod == SftpAuthMethod.PrivateKey ? "privateKey" : "password",
            };
        }
    }

    // MARK: - Connecting

    /// <summary>
    /// Connects with what the user entered, verifies by listing the remote path (as macOS
    /// does), pins the host key and stores everything. A changed host key on an existing
    /// account is shown with both fingerprints and accepted only on an explicit yes.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var host = CleanHost(values.GetTrimmed("host"));
        var port = values.GetInt("port", 22);
        var usesKey = values.Mode == "privateKey";
        var privateKey = usesKey ? values.Get("privateKey") : null;
        if (usesKey)
        {
            var trimmedKey = privateKey!.Trim();
            if (!trimmedKey.StartsWith("-----BEGIN", StringComparison.Ordinal) &&
                !trimmedKey.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
            {
                throw CloudProviderException.CommandFailed(Localization.L("That doesn't look like a PEM private key. Expected file to start with -----BEGIN."));
            }
        }

        var credentials = new SftpCredentials
        {
            Host = host,
            Port = port is > 0 and <= 65535 ? port : 22,
            Username = values.GetTrimmed("username"),
            AuthMethod = usesKey ? SftpAuthMethod.PrivateKey : SftpAuthMethod.Password,
            Password = usesKey ? null : values.Get("password"),
            PrivateKey = privateKey,
            Passphrase = usesKey ? values.GetOptional("passphrase") is null ? null : values.Get("passphrase") : null,
            RemotePath = values.GetTrimmed("remotePath", "/"),
        };

        // The pinned key carries over when the account still points at the same server.
        string? expected;
        lock (_gate)
        {
            expected = _credentials is { } old &&
                       string.Equals(old.Host, credentials.Host, StringComparison.OrdinalIgnoreCase) &&
                       old.Port == credentials.Port
                ? old.HostKeyFingerprint
                : null;
        }

        var check = new SftpHostKeyCheck(expected);
        ISftpTransport transport;
        try
        {
            transport = await _factory(credentials, check, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && check.Mismatch)
        {
            var confirmed = await prompt.ConfirmAsync(
                Localization.L("Host key changed"),
                Localization.L(
                    "The SSH host key of {0} has changed since you last connected. This happens when the server was reinstalled — or when someone intercepts the connection.\n\nPreviously: {1}\nNow: {2}\n\nTrust the new key?",
                    credentials.Host,
                    expected ?? string.Empty,
                    check.Presented ?? string.Empty),
                cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                throw SftpErrors.HostKeyChanged();
            }

            // Pin exactly the key the user just saw, not whatever comes next.
            check = new SftpHostKeyCheck(check.Presented);
            try
            {
                transport = await _factory(credentials, check, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retry) when (retry is not OperationCanceledException)
            {
                throw SftpErrors.Map(retry, null, credentials.AuthMethod, check);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw SftpErrors.Map(e, null, credentials.AuthMethod, check);
        }

        string remotePath;
        try
        {
            remotePath = ResolveRemotePath(credentials.RemotePath, transport.WorkingDirectory);

            // Verify by listing the chosen folder: the login can succeed while the folder is
            // missing or unreadable, and that must fail here, not as an empty panel later.
            await transport.ListAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            transport.Dispose();
            if (e is OperationCanceledException)
            {
                throw;
            }

            throw SftpErrors.Map(e, credentials.RemotePath, credentials.AuthMethod);
        }

        credentials = credentials with { RemotePath = remotePath, HostKeyFingerprint = check.Presented ?? expected };
        Credentials.Save(CredentialKey, credentials);
        ISftpTransport? previous;
        lock (_gate)
        {
            previous = _transport;
            _credentials = credentials;
            _transport = transport;
            _shellCopy = 0;
        }

        previous?.Dispose();
        SupportLogger.Shared.Log(
            $"Authenticated as {credentials.Username}@{credentials.Host}:{credentials.Port} via {credentials.AuthMethod}; host key {credentials.HostKeyFingerprint}",
            LogCategory);
        return new ConnectResult($"{credentials.Username}@{credentials.Host}") { RootPath = remotePath };
    }

    public override Task DisconnectAsync()
    {
        ISftpTransport? previous;
        lock (_gate)
        {
            previous = _transport;
            _transport = null;
            _credentials = null;
        }

        previous?.Dispose();
        return base.DisconnectAsync();
    }

    /// <summary>"sftp://host:22/x" → "host"; anything else unchanged.</summary>
    internal static string CleanHost(string host)
    {
        var value = host.Trim();
        if (value.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            value = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var slash = value.IndexOf('/');
            if (slash >= 0)
            {
                value = value[..slash];
            }

            var colon = value.LastIndexOf(':');
            if (colon > 0 && value.IndexOf(':') == colon)
            {
                value = value[..colon];
            }
        }

        var at = value.LastIndexOf('@');
        return at >= 0 ? value[(at + 1)..] : value;
    }

    /// <summary>
    /// The panel's start folder as an absolute path: empty → "/", "~" and relative paths are
    /// taken from the login directory (the OpenSSH client resolved them there on macOS).
    /// </summary>
    internal static string ResolveRemotePath(string remotePath, string workingDirectory)
    {
        var value = remotePath.Trim();
        if (value.Length == 0)
        {
            return "/";
        }

        if (value.StartsWith('/'))
        {
            return CloudPath.Normalize(value);
        }

        var home = string.IsNullOrEmpty(workingDirectory) ? "/" : workingDirectory;
        if (value == "~")
        {
            return CloudPath.Normalize(home);
        }

        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return CloudPath.Normalize(home + "/" + value);
    }

    // MARK: - Session

    /// <summary>The live session, (re)connecting single-flight when there is none or it dropped.</summary>
    private async Task<ISftpTransport> TransportAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_credentials is null)
            {
                throw CloudProviderException.NotAuthenticated();
            }

            if (_transport is { IsConnected: true } live)
            {
                return live;
            }
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SftpCredentials credentials;
            ISftpTransport? stale;
            lock (_gate)
            {
                credentials = _credentials ?? throw CloudProviderException.NotAuthenticated();
                if (_transport is { IsConnected: true } live)
                {
                    return live;
                }

                stale = _transport;
                _transport = null;
            }

            stale?.Dispose();
            var check = new SftpHostKeyCheck(credentials.HostKeyFingerprint);
            ISftpTransport fresh;
            try
            {
                fresh = await _factory(credentials, check, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SupportLogger.Shared.Log($"Connect to {credentials.Host}:{credentials.Port} failed: {e.GetType().Name}: {e.Message}", LogCategory, LogLevel.Error);
                throw SftpErrors.Map(e, null, credentials.AuthMethod, check);
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_credentials, credentials))
                {
                    // Re-connected (Edit Account) meanwhile; that session wins.
                    fresh.Dispose();
                    return _transport ?? throw CloudProviderException.NotAuthenticated();
                }

                // Trust on first use for accounts stored before the key was pinned.
                if (credentials.HostKeyFingerprint is null && check.Presented is not null)
                {
                    _credentials = credentials with { HostKeyFingerprint = check.Presented };
                    Credentials.Save(CredentialKey, _credentials);
                    SupportLogger.Shared.Log($"Pinned host key {check.Presented} for {credentials.Host}", LogCategory);
                }

                _transport = fresh;
            }

            return fresh;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void Retire(ISftpTransport transport)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_transport, transport))
            {
                return;
            }

            _transport = null;
        }

        transport.Dispose();
    }

    private SftpAuthMethod AuthMethod
    {
        get
        {
            lock (_gate)
            {
                return _credentials?.AuthMethod ?? SftpAuthMethod.Password;
            }
        }
    }

    /// <summary>
    /// Runs one operation on the session. A dropped session is retired; read-only and
    /// idempotent operations (<paramref name="retry"/>) then get one more try on a fresh one.
    /// </summary>
    private async Task<T> RunAsync<T>(string? path, Func<ISftpTransport, Task<T>> operation, CancellationToken cancellationToken, bool retry = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var transport = await TransportAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(transport).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CloudProviderException)
            {
                throw;
            }
            catch (Exception e) when (SftpErrors.IsConnectionLost(e))
            {
                Retire(transport);
                SupportLogger.Shared.Log($"Session lost ({e.GetType().Name}: {e.Message})", LogCategory, LogLevel.Notice);
                if (retry && attempt == 0)
                {
                    continue;
                }

                throw SftpErrors.Map(e, path, AuthMethod);
            }
            catch (Exception e)
            {
                throw SftpErrors.Map(e, path, AuthMethod);
            }
        }
    }

    // MARK: - Listing

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        var items = await RunAsync(normalized, async transport =>
        {
            var entries = await transport.ListAsync(normalized, cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveLinksAsync(transport, normalized, Clean(entries), cancellationToken).ConfigureAwait(false);
            return resolved.Select(e => ToItem(normalized, e)).ToList();
        }, cancellationToken, retry: true).ConfigureAwait(false);

        // Diagnostic trail for "the folder looks empty" reports (#31/#42): the Support Log shows
        // whether a folder really is empty.
        SupportLogger.Shared.Log($"listed {items.Count} item(s) for {normalized}", LogCategory);
        return items;
    }

    /// <summary>
    /// Drops "." and "..", and keeps only the last component of a name a server printed with
    /// a path (issue #31: "/VERSION", "/html/.htaccess" — a file name can't contain "/").
    /// </summary>
    internal static IReadOnlyList<SftpEntry> Clean(IEnumerable<SftpEntry> entries)
    {
        var result = new List<SftpEntry>();
        foreach (var entry in entries)
        {
            var name = entry.Name;
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name[(slash + 1)..];
            }

            if (name is "" or "." or "..")
            {
                continue;
            }

            result.Add(name == entry.Name ? entry : entry with { Name = name });
        }

        return result;
    }

    /// <summary>
    /// Follows each symlink to show what it points at: a link to a folder becomes a navigable
    /// folder, a link to a file shows the file's size and date. A dangling or unreadable link
    /// stays a plain file entry, as the macOS listing showed it.
    /// </summary>
    private static async Task<IReadOnlyList<(SftpEntry Entry, bool IsLink)>> ResolveLinksAsync(
        ISftpTransport transport, string directory, IReadOnlyList<SftpEntry> entries, CancellationToken cancellationToken)
    {
        if (!entries.Any(e => e.IsSymlink))
        {
            return entries.Select(e => (e, false)).ToList();
        }

        using var throttle = new SemaphoreSlim(LinkResolveConcurrency);
        var tasks = entries.Select(async entry =>
        {
            if (!entry.IsSymlink)
            {
                return (entry, false);
            }

            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var target = await transport.StatAsync(CloudPath.Combine(directory, entry.Name), cancellationToken).ConfigureAwait(false);
                return (entry with { IsDirectory = target.IsDirectory, Size = target.Size, LastWriteUtc = target.LastWriteUtc }, true);
            }
            catch (Exception e) when (e is not OperationCanceledException && !SftpErrors.IsConnectionLost(e))
            {
                return (entry with { IsDirectory = false }, true);
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static CloudFileItem ToItem(string directory, (SftpEntry Entry, bool IsLink) resolved)
    {
        var (entry, isLink) = resolved;
        var path = CloudPath.Combine(directory, entry.Name);
        return new CloudFileItem(path, entry.Name, path, entry.IsDirectory, entry.IsDirectory ? 0 : Math.Max(0, entry.Size), ToDate(entry.LastWriteUtc))
        {
            IsHidden = CloudFileItem.IsDotHidden(entry.Name),
            IsSymlink = isLink || entry.IsSymlink,
        };
    }

    private static DateTimeOffset ToDate(DateTime utc)
    {
        if (utc <= DateTime.UnixEpoch)
        {
            return CloudFileItem.UnknownDate;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        if (normalized == "/")
        {
            return Task.FromResult(new CloudFileItem("/", string.Empty, "/", isDirectory: true, 0, CloudFileItem.UnknownDate));
        }

        return RunAsync(normalized, async transport =>
        {
            var entry = await transport.LStatAsync(normalized, cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveLinksAsync(transport, CloudPath.Parent(normalized), [entry with { Name = CloudPath.Name(normalized) }], cancellationToken).ConfigureAwait(false);
            return ToItem(CloudPath.Parent(normalized), resolved[0]);
        }, cancellationToken, retry: true);
    }

    /// <summary>Total of the files beneath; symlinked folders are not entered (a link to an ancestor would never end).</summary>
    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(path, async transport =>
        {
            long total = 0;
            await WalkAsync(transport, CloudPath.Normalize(path), (_, entry) => total += entry.IsDirectory ? 0 : Math.Max(0, entry.Size), cancellationToken).ConfigureAwait(false);
            return total;
        }, cancellationToken, retry: true);

    /// <summary>Recursive name search over the listing, as macOS does; symlinked folders are not entered.</summary>
    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await RunAsync(path, async transport =>
        {
            var results = new List<CloudFileItem>();
            await WalkAsync(transport, CloudPath.Normalize(path ?? "/"), (directory, entry) =>
            {
                if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(ToItem(directory, (entry, entry.IsSymlink)));
                }
            }, cancellationToken).ConfigureAwait(false);
            return results;
        }, cancellationToken, retry: true).ConfigureAwait(false);

    /// <summary>Depth-first walk of real folders, reporting every entry as listed (links not followed).</summary>
    private static async Task WalkAsync(ISftpTransport transport, string root, Action<string, SftpEntry> visit, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Clean(await transport.ListAsync(directory, cancellationToken).ConfigureAwait(false)))
            {
                visit(directory, entry);
                if (entry.IsDirectory && !entry.IsSymlink)
                {
                    pending.Push(CloudPath.Combine(directory, entry.Name));
                }
            }
        }
    }

    // MARK: - Transfers

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => RunAsync<bool>(remotePath, async transport =>
        {
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Staged and renamed, so a failed or cancelled download never leaves a truncated
            // file that looks complete.
            var partial = localPath + ".fileflusspart";
            try
            {
                var progress = new CumulativeProgress(onBytes);
                await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, HttpTransfer.BufferSize, useAsync: true))
                {
                    await transport.DownloadAsync(CloudPath.Normalize(remotePath), target, progress.Report, cancellationToken).ConfigureAwait(false);
                }

                File.Move(partial, localPath, overwrite: true);
                return true;
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }, cancellationToken);

    /// <summary>
    /// Uploads, then stamps the local modification time on the remote file — macOS used
    /// <c>put -p</c> so sync comparisons stay stable across re-uploads.
    /// </summary>
    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => RunAsync<bool>(remotePath, async transport =>
        {
            var target = CloudPath.Normalize(remotePath);
            var progress = new CumulativeProgress(onBytes);
            await using (var source = HttpTransfer.OpenForUpload(localPath))
            {
                await transport.UploadAsync(source, target, progress.Report, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await transport.SetLastWriteTimeUtcAsync(target, File.GetLastWriteTimeUtc(localPath), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException && !SftpErrors.IsConnectionLost(e))
            {
                SupportLogger.Shared.Log($"Could not keep the modification time of {target}: {e.Message}", LogCategory, LogLevel.Notice);
            }

            return true;
        }, cancellationToken);

    // MARK: - File operations

    /// <summary>Quietly succeeds when the folder already exists.</summary>
    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync<bool>(path, async transport =>
        {
            var normalized = CloudPath.Normalize(path);
            try
            {
                await transport.CreateDirectoryAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException && !SftpErrors.IsConnectionLost(e))
            {
                // MKDIR on an existing folder fails with a generic status; only rethrow when the
                // folder really isn't there.
                SftpEntry? existing;
                try
                {
                    existing = await transport.StatAsync(normalized, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception stat) when (stat is not OperationCanceledException && !SftpErrors.IsConnectionLost(stat))
                {
                    existing = null;
                }

                if (existing is not { IsDirectory: true })
                {
                    throw;
                }
            }

            return true;
        }, cancellationToken, retry: true);

    /// <summary>
    /// A file or link is removed itself; a folder is emptied depth-first (links inside are
    /// removed, never followed) and then removed.
    /// </summary>
    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        if (normalized == "/")
        {
            throw CloudProviderException.CommandFailed(Localization.L("Permission denied: {0}", "/"));
        }

        return RunAsync<bool>(normalized, async transport =>
        {
            var entry = await transport.LStatAsync(normalized, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(transport, normalized, entry, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private static async Task DeleteAsync(ISftpTransport transport, string path, SftpEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.IsSymlink || !entry.IsDirectory)
        {
            await transport.RemoveAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var child in Clean(await transport.ListAsync(path, cancellationToken).ConfigureAwait(false)))
        {
            await DeleteAsync(transport, CloudPath.Combine(path, child.Name), child, cancellationToken).ConfigureAwait(false);
        }

        await transport.RemoveDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => MoveItemAsync(path, CloudPath.Combine(CloudPath.Parent(path), newName), cancellationToken);

    /// <summary>Server-side via SSH_FXP_RENAME, which works across folders. Fails rather than overwrites when the target exists.</summary>
    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => RunAsync<bool>(path, async transport =>
        {
            await transport.RenameAsync(CloudPath.Normalize(path), CloudPath.Normalize(newPath), cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => RunAsync<bool>(path, async transport =>
        {
            await transport.SetLastWriteTimeUtcAsync(CloudPath.Normalize(path), date.UtcDateTime, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken, retry: true);

    /// <summary>
    /// Server-side copy with <c>cp -Rp</c> on an SSH exec channel (issue #62). Accounts without a
    /// shell (internal-sftp, chroot, Windows OpenSSH) answer NotImplemented, and the caller
    /// copies through the client. The target must not exist: <c>cp -R</c> onto an existing
    /// folder would nest the copy inside it.
    /// </summary>
    public override async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _shellCopy) == 2)
        {
            throw CloudProviderException.NotImplemented();
        }

        var source = CloudPath.Normalize(path);
        var target = CloudPath.Normalize(newPath);
        await RunAsync<bool>(source, async transport =>
        {
            if (await ExistsAsync(transport, target, cancellationToken).ConfigureAwait(false))
            {
                throw CloudProviderException.CommandFailed(Localization.L("An item named “{0}” already exists at the destination.", CloudPath.Name(target)));
            }

            if (!await ShellCopyAvailableAsync(transport, cancellationToken).ConfigureAwait(false))
            {
                throw CloudProviderException.NotImplemented();
            }

            SftpExecResult result;
            try
            {
                result = await transport.ExecuteAsync($"cp -Rp -- {ShellQuote(source)} {ShellQuote(target)}", null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException && !SftpErrors.IsConnectionLost(e))
            {
                // The exec channel was refused after all.
                Volatile.Write(ref _shellCopy, 2);
                throw CloudProviderException.NotImplemented();
            }

            if (result.ExitStatus is 126 or 127)
            {
                Volatile.Write(ref _shellCopy, 2);
                throw CloudProviderException.NotImplemented();
            }

            if (result.ExitStatus != 0)
            {
                var message = FirstLines(result.Error) ?? FirstLines(result.Output) ??
                              Localization.L("SFTP command failed.");
                SupportLogger.Shared.Log($"cp failed (exit {result.ExitStatus}): {message}", LogCategory, LogLevel.Error);
                throw CloudProviderException.CommandFailed(message);
            }

            if (!await ExistsAsync(transport, target, cancellationToken).ConfigureAwait(false))
            {
                throw CloudProviderException.CommandFailed(Localization.L("SFTP command failed."));
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes once per session with <c>command -v cp</c>: a POSIX shell answers with a path and
    /// exit 0; an SFTP-only account refuses the channel, prints its "sftp only" notice, or (with
    /// ForceCommand internal-sftp) runs the SFTP server instead — which the timeout catches.
    /// </summary>
    private async Task<bool> ShellCopyAvailableAsync(ISftpTransport transport, CancellationToken cancellationToken)
    {
        var known = Volatile.Read(ref _shellCopy);
        if (known != 0)
        {
            return known == 1;
        }

        bool available;
        try
        {
            var probe = await transport.ExecuteAsync("command -v cp", ShellProbeTimeout, cancellationToken).ConfigureAwait(false);
            available = probe.ExitStatus == 0 && probe.Output.Trim().Length > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SupportLogger.Shared.Log($"No shell for server-side copy: {e.GetType().Name}: {e.Message}", LogCategory, LogLevel.Notice);
            available = false;
        }

        Volatile.Write(ref _shellCopy, available ? 1 : 2);
        return available;
    }

    private static async Task<bool> ExistsAsync(ISftpTransport transport, string path, CancellationToken cancellationToken)
    {
        try
        {
            await transport.LStatAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return false;
        }
    }

    /// <summary>POSIX single quotes: nothing inside is special except the quote itself.</summary>
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? FirstLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).ToList();
        return lines.Count == 0 ? null : string.Join(" — ", lines);
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ISftpTransport? transport;
            lock (_gate)
            {
                transport = _transport;
                _transport = null;
            }

            transport?.Dispose();
            _connectGate.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Turns the library's cumulative byte count into the deltas <see cref="ByteProgress"/> wants.</summary>
    private sealed class CumulativeProgress(ByteProgress? onBytes)
    {
        private readonly Lock _gate = new();
        private long _reported;

        public void Report(long total)
        {
            if (onBytes is null)
            {
                return;
            }

            long delta;
            lock (_gate)
            {
                delta = total - _reported;
                if (delta <= 0)
                {
                    return;
                }

                _reported = total;
            }

            onBytes(delta);
        }
    }
}
