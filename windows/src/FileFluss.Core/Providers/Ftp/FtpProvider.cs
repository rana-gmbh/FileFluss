// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Ftp;

/// <summary>
/// Plain FTP and explicit FTPS. macOS shells out to curl; this uses FluentFTP behind
/// <see cref="IFtpConnection"/>, so the provider logic — path mapping, errors, the tree walks —
/// is testable without a server.
///
/// <para><b>Paths.</b> The account is rooted at the FTP login directory: "/" in the panel is
/// wherever the server put the user after login (PWD), and "/pub" is "pub" beneath it — curl's
/// relative URLs on macOS did exactly that. The configured remote path is the folder the panel
/// opens at, inside that root.</para>
/// </summary>
[CloudProvider(CloudProviderType.Ftp)]
public sealed class FtpProvider : CloudProvider
{
    private const string LogCategory = "ftp";

    private readonly ConnectionFactory _factory;
    private readonly Lock _gate = new();
    private Session? _session;

    public FtpProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    /// <summary>Test seam: tests pass a factory of scripted connections; null → FluentFTP.</summary>
    internal FtpProvider(Guid accountId, ICredentialStore credentials, ConnectionFactory? factory)
        : base(accountId, credentials)
    {
        _factory = factory ?? FluentFtpConnection.ConnectAsync;
        var stored = Credentials.Load<FtpCredentials>(CredentialKey);
        if (stored is { Host.Length: > 0, Username.Length: > 0 })
        {
            _session = new Session(stored, _factory);
        }
    }

    internal delegate Task<IFtpConnection> ConnectionFactory(FtpCredentials credentials, CancellationToken cancellationToken);

    public override CloudProviderType ProviderType => CloudProviderType.Ftp;

    protected override string CredentialKeyPrefix => "ftp";

    public override bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public override ConnectSpec ConnectSpec => new(
        new ConnectField("host", ConnectFieldKind.Text, "Server") { Placeholder = "Server (e.g. ftp.example.com)" },
        new ConnectField("port", ConnectFieldKind.Number, "Port") { DefaultValue = "21" },
        new ConnectField("username", ConnectFieldKind.Text, "Username"),
        new ConnectField("password", ConnectFieldKind.Password, "Password"),
        new ConnectField("remotePath", ConnectFieldKind.Text, "Initial path") { Placeholder = "Initial path (optional)", DefaultValue = "/", Required = false },
        new ConnectField("useTLS", ConnectFieldKind.Checkbox, "Use FTPS (TLS)") { DefaultValue = "false", Required = false },
        // Only meaningful with FTPS; ignored for plain FTP.
        new ConnectField("allowInvalidCertificate", ConnectFieldKind.Checkbox, "Allow self-signed certificate") { DefaultValue = "false", Required = false },
        ConnectField.Warning("Plain FTP sends credentials and data unencrypted. Enable FTPS when the server supports it."));

    private Session Current
    {
        get
        {
            lock (_gate)
            {
                return _session ?? throw CloudProviderException.NotAuthenticated();
            }
        }
    }

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
    {
        lock (_gate)
        {
            if (_session?.Credentials is not { } c)
            {
                return new Dictionary<string, string>();
            }

            return new Dictionary<string, string>
            {
                ["host"] = c.Host,
                ["port"] = c.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["username"] = c.Username,
                ["remotePath"] = c.RemotePath,
                ["useTLS"] = c.UseTls ? "true" : "false",
                ["allowInvalidCertificate"] = c.AllowInvalidCertificate ? "true" : "false",
            };
        }
    }

    // MARK: - Connecting

    /// <summary>Logs in and verifies by opening the landing folder, as macOS does, before anything is stored.</summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var host = CleanHost(values.GetTrimmed("host"));
        var port = values.GetInt("port", 21);
        var useTls = values.GetBool("useTLS");
        var credentials = new FtpCredentials
        {
            Host = host,
            Port = port is > 0 and <= 65535 ? port : 21,
            Username = values.GetTrimmed("username"),
            Password = values.Get("password"),
            RemotePath = CloudPath.Normalize(values.GetTrimmed("remotePath", "/")),
            UseTls = useTls,
            AllowInvalidCertificate = useTls && values.GetBool("allowInvalidCertificate"),
        };

        IFtpConnection connection;
        try
        {
            connection = await _factory(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw FtpErrors.Map(e, null);
        }

        var session = new Session(credentials, _factory);
        try
        {
            session.Home = await HomeOfAsync(connection, cancellationToken).ConfigureAwait(false);
            var landing = ServerPath(session.Home, credentials.RemotePath);
            if (!CloudPath.IsRoot(credentials.RemotePath) && !await connection.DirectoryExistsAsync(landing, cancellationToken).ConfigureAwait(false))
            {
                throw CloudProviderException.NotFound(credentials.RemotePath);
            }

            await connection.ListAsync(landing, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            connection.Dispose();
            session.Dispose();
            if (e is OperationCanceledException)
            {
                throw;
            }

            throw FtpErrors.Map(e, credentials.RemotePath);
        }

        session.Pool.Adopt(connection);
        Credentials.Save(CredentialKey, credentials);
        Session? previous;
        lock (_gate)
        {
            previous = _session;
            _session = session;
        }

        previous?.Dispose();
        SupportLogger.Shared.Log($"Authenticated as {credentials.Username}@{host}:{credentials.Port} (TLS: {useTls})", LogCategory);
        return new ConnectResult($"{credentials.Username}@{host}") { RootPath = credentials.RemotePath };
    }

    public override Task DisconnectAsync()
    {
        Session? previous;
        lock (_gate)
        {
            previous = _session;
            _session = null;
        }

        previous?.Dispose();
        return base.DisconnectAsync();
    }

    /// <summary>"ftp://ftp.example.com:21/x" → "ftp.example.com" — macOS would have built a broken URL from it.</summary>
    internal static string CleanHost(string host)
    {
        var value = host.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        var slash = value.IndexOf('/');
        if (slash >= 0)
        {
            value = value[..slash];
        }

        // A typed ":port" belongs in the port field; keep IPv6 literals ("[::1]") intact.
        var colon = value.LastIndexOf(':');
        if (colon > 0 && !value.EndsWith(']') && value.IndexOf(':') == colon)
        {
            value = value[..colon];
        }

        return value;
    }

    // MARK: - Paths

    /// <summary>App path → absolute server path under the login directory.</summary>
    internal static string ServerPath(string home, string path)
    {
        var normalized = CloudPath.Normalize(path);
        var root = home.TrimEnd('/');
        if (normalized == "/")
        {
            return root.Length == 0 ? "/" : root;
        }

        return root + normalized;
    }

    /// <summary>The login directory from PWD; "/" when the server won't say or answers with a non-POSIX path.</summary>
    private static async Task<string> HomeOfAsync(IFtpConnection connection, CancellationToken cancellationToken)
    {
        string? home;
        try
        {
            home = (await connection.GetWorkingDirectoryAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException && !FtpErrors.IsConnectionLevel(e))
        {
            home = null;
        }

        return string.IsNullOrEmpty(home) || !home.StartsWith('/') ? "/" : home;
    }

    /// <summary>
    /// Runs one operation on a leased connection. A server reply (550 …) keeps the connection
    /// for reuse; a transport failure or a cancelled transfer retires it.
    /// </summary>
    private async Task<T> RunAsync<T>(string? path, Func<IFtpConnection, string, Task<T>> operation, CancellationToken cancellationToken)
    {
        var session = Current;
        IFtpConnection connection;
        try
        {
            connection = await session.Pool.LeaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw FtpErrors.Map(e, path);
        }

        var reusable = true;
        try
        {
            session.Home ??= await HomeOfAsync(connection, cancellationToken).ConfigureAwait(false);
            return await operation(connection, session.Home).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reusable = false;
            throw;
        }
        catch (CloudProviderException)
        {
            throw;
        }
        catch (Exception e)
        {
            reusable = !FtpErrors.IsConnectionLevel(e);
            throw FtpErrors.Map(e, path);
        }
        finally
        {
            session.Pool.Return(connection, reusable);
        }
    }

    // MARK: - Listing

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        return RunAsync(normalized, (connection, home) => ListAsync(connection, home, normalized, cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<CloudFileItem>> ListAsync(IFtpConnection connection, string home, string path, CancellationToken cancellationToken)
    {
        var entries = await connection.ListAsync(ServerPath(home, path), cancellationToken).ConfigureAwait(false);
        return ToItems(entries, path, connection.ListsInUtc);
    }

    /// <summary>
    /// Listing rows → items. "." and ".." go; a name printed with a path keeps only its last
    /// component (some servers print full paths, as SFTP's issue #31 showed). Links count as
    /// files, as the macOS listing parser treated them.
    /// </summary>
    internal static IReadOnlyList<CloudFileItem> ToItems(IEnumerable<FtpEntry> entries, string basePath, bool utc)
    {
        var items = new List<CloudFileItem>();
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

            var isDirectory = entry.Kind == FtpEntryKind.Directory;
            var itemPath = CloudPath.Combine(basePath, name);
            items.Add(new CloudFileItem(itemPath, name, itemPath, isDirectory, isDirectory ? 0 : entry.Size, ToDate(entry.Modified, utc))
            {
                IsHidden = CloudFileItem.IsDotHidden(name),
                IsSymlink = entry.Kind == FtpEntryKind.Link,
            });
        }

        return items;
    }

    /// <summary>MLSD times are UTC; LIST times are the server's wall clock, read as local time like macOS did.</summary>
    private static DateTimeOffset ToDate(DateTime value, bool utc)
    {
        if (value == DateTime.MinValue)
        {
            return CloudFileItem.UnknownDate;
        }

        try
        {
            return utc
                ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
        }
        catch (ArgumentOutOfRangeException)
        {
            return CloudFileItem.UnknownDate;
        }
    }

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        if (normalized == "/")
        {
            return new CloudFileItem("/", string.Empty, "/", isDirectory: true, 0, CloudFileItem.UnknownDate);
        }

        var name = CloudPath.Name(normalized);
        var siblings = await ListDirectoryAsync(CloudPath.Parent(normalized), cancellationToken).ConfigureAwait(false);
        return siblings.FirstOrDefault(i => i.Name == name) ?? throw CloudProviderException.NotFound(path);
    }

    /// <summary>Recursive name search over the listing, as macOS does — FTP has no server-side search.</summary>
    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
    {
        var results = new List<CloudFileItem>();
        var pending = new Stack<string>();
        pending.Push(path ?? "/");
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in await ListDirectoryAsync(pending.Pop(), cancellationToken).ConfigureAwait(false))
            {
                if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(item);
                }

                if (item.IsDirectory)
                {
                    pending.Push(item.Path);
                }
            }
        }

        return results;
    }

    // MARK: - Transfers

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => RunAsync<bool>(remotePath, async (connection, home) =>
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
                    await connection.DownloadAsync(ServerPath(home, remotePath), target, progress.Report, cancellationToken).ConfigureAwait(false);
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

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => RunAsync<bool>(remotePath, async (connection, home) =>
        {
            var target = ServerPath(home, remotePath);
            long sent = 0;
            if (!await TryUploadStagedAsync(connection, localPath, target, d => { sent += d; onBytes?.Invoke(d); }, cancellationToken).ConfigureAwait(false))
            {
                // The server refused the staging name or the rename: write in place, as before.
                var progress = new CumulativeProgress(onBytes, sent);
                await using var source = HttpTransfer.OpenForUpload(localPath);
                await connection.UploadAsync(source, target, progress.Report, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken);

    /// <summary>
    /// Uploads to "&lt;name&gt;.fileflusspart" and renames it over the target (RNTO replaces on
    /// most servers; where it refuses, the old file is deleted only once the new one is
    /// complete), so a failed or cancelled upload never truncates the file it replaces.
    /// Returns false, staging file removed, when the server refuses that route outright.
    /// </summary>
    private static async Task<bool> TryUploadStagedAsync(IFtpConnection connection, string localPath, string target, ByteProgress onBytes, CancellationToken cancellationToken)
    {
        var partial = target + ".fileflusspart";
        var progress = new CumulativeProgress(onBytes);
        try
        {
            await using var source = HttpTransfer.OpenForUpload(localPath);
            await connection.UploadAsync(source, partial, progress.Report, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && !FtpErrors.IsConnectionLevel(e) && progress.Total == 0)
        {
            await TryDeleteAsync(connection, partial).ConfigureAwait(false);
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException && !FtpErrors.IsConnectionLevel(e))
        {
            // A cancelled or broken transfer leaves the control connection in no state for
            // another command (it is retired); the "*.fileflusspart" name marks the leftover.
            await TryDeleteAsync(connection, partial).ConfigureAwait(false);
            throw;
        }

        try
        {
            await connection.RenameAsync(partial, target, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException && !FtpErrors.IsConnectionLevel(e))
        {
            try
            {
                await connection.DeleteFileAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception delete) when (delete is not OperationCanceledException && !FtpErrors.IsConnectionLevel(delete))
            {
                // Nothing to replace (or not ours to delete): the rename itself is refused.
                await TryDeleteAsync(connection, partial).ConfigureAwait(false);
                return false;
            }

            await connection.RenameAsync(partial, target, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private static async Task TryDeleteAsync(IFtpConnection connection, string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await connection.DeleteFileAsync(path, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Best effort: a clearly named leftover, and the connection may be gone already.
        }
    }

    // MARK: - File operations

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync<bool>(path, async (connection, home) =>
        {
            await connection.CreateDirectoryAsync(ServerPath(home, path), cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>RMD only removes empty folders, so a folder's contents go first — on one connection, depth first.</summary>
    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        if (normalized == "/")
        {
            throw CloudProviderException.CommandFailed(Localization.L("FTP command failed."));
        }

        return RunAsync<bool>(normalized, async (connection, home) =>
        {
            var name = CloudPath.Name(normalized);
            var siblings = await ListAsync(connection, home, CloudPath.Parent(normalized), cancellationToken).ConfigureAwait(false);
            var item = siblings.FirstOrDefault(i => i.Name == name) ?? throw CloudProviderException.NotFound(path);
            await DeleteAsync(connection, home, item, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private static async Task DeleteAsync(IFtpConnection connection, string home, CloudFileItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!item.IsDirectory)
        {
            await connection.DeleteFileAsync(ServerPath(home, item.Path), cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var child in await ListAsync(connection, home, item.Path, cancellationToken).ConfigureAwait(false))
        {
            await DeleteAsync(connection, home, child, cancellationToken).ConfigureAwait(false);
        }

        await connection.RemoveDirectoryAsync(ServerPath(home, item.Path), cancellationToken).ConfigureAwait(false);
    }

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => MoveItemAsync(path, CloudPath.Combine(CloudPath.Parent(path), newName), cancellationToken);

    /// <summary>RNFR/RNTO, which servers also accept across folders — a server-side move.</summary>
    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => RunAsync<bool>(path, async (connection, home) =>
        {
            await connection.RenameAsync(ServerPath(home, path), ServerPath(home, newPath), cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>MFMT where the server offers it; otherwise NotImplemented (callers treat it as best-effort).</summary>
    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => RunAsync<bool>(path, async (connection, home) =>
        {
            if (!connection.CanSetModifiedTime)
            {
                throw CloudProviderException.NotImplemented();
            }

            await connection.SetModifiedTimeAsync(ServerPath(home, path), date.UtcDateTime, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

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
            Session? previous;
            lock (_gate)
            {
                previous = _session;
                _session = null;
            }

            previous?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Credentials, the pool logged in with them, and the login directory once known.</summary>
    private sealed class Session(FtpCredentials credentials, ConnectionFactory factory) : IDisposable
    {
        public FtpCredentials Credentials { get; } = credentials;

        public FtpConnectionPool Pool { get; } = new(ct => factory(credentials, ct));

        public string? Home { get; set; }

        public void Dispose() => Pool.Dispose();
    }

    /// <summary>Turns the library's cumulative byte count into the deltas <see cref="ByteProgress"/> wants.</summary>
    private sealed class CumulativeProgress(ByteProgress? onBytes, long alreadyReported = 0)
    {
        private readonly Lock _gate = new();
        private long _reported = alreadyReported;

        /// <summary>The highest running total seen so far.</summary>
        public long Total
        {
            get
            {
                lock (_gate)
                {
                    return _reported;
                }
            }
        }

        public void Report(long total)
        {
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

            onBytes?.Invoke(delta);
        }
    }
}
