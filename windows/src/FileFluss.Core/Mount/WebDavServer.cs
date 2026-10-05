// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Mount;

/// <summary>How one <see cref="WebDavServer"/> serves its provider.</summary>
public sealed record WebDavServerOptions
{
    /// <summary>The provider folder the drive's root shows (the account's root path).</summary>
    public string MountRoot { get; init; } = "/";

    /// <summary>The root's <c>displayname</c>.</summary>
    public string DisplayName { get; init; } = "FileFluss";

    /// <summary>Downloaded bytes kept for re-reads; null downloads on every GET.</summary>
    public WebDavReadCache? ReadCache { get; init; }

    /// <summary>Where file-manager metadata files go; null discards them like /dev/null.</summary>
    public WebDavSidecarStore? Sidecar { get; init; }

    /// <summary>The secret first path segment; null makes a fresh one (<see cref="WebDavServer.NewSecret"/>).</summary>
    public string? Secret { get; init; }

    /// <summary>Where request bodies and uncached downloads are staged; null = a folder under <see cref="StagingLocation"/>.</summary>
    public string? TempDirectory { get; init; }

    /// <summary>
    /// Whether an upload onto an existing path replaces the file in place on this provider
    /// (WebDAV, S3, SFTP, Google Drive's cached-id PATCH). When false, overwriting goes
    /// through a temporary sibling — upload, delete the old file, rename — because some
    /// providers reject an existing name (Dropbox uploads in "add" mode) and others would
    /// keep two files of the same name; the price is that the file's identity (share links,
    /// version history) doesn't survive the overwrite.
    /// </summary>
    public bool UploadReplacesInPlace { get; init; }

    /// <summary>How long a folder listing answers repeated questions; see <see cref="WebDavMetadataCache"/>.</summary>
    public TimeSpan MetadataLifetime { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A loopback WebDAV server exposing one <see cref="FileProvider"/> (rooted at a chosen
/// folder) over HTTP, so Windows' built-in WebDAV redirector (the WebClient service) can map
/// it as a drive letter in File Explorer — the Windows counterpart of the macOS
/// <c>WebDAVServer</c> behind "Mount in Finder". Every request is translated 1:1 to the
/// provider's API.
///
/// <para><b>What it speaks</b> is what the redirector, Explorer and Office actually send:
/// OPTIONS (<c>DAV: 1, 2</c>, <c>MS-Author-Via: DAV</c>), PROPFIND Depth 0/1 with the DAV
/// properties plus Microsoft's <c>Win32FileAttributes</c> and Win32 times, GET with byte
/// ranges, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, PROPPATCH (Win32 times; the modification
/// time is passed on to the provider best-effort) and LOCK/UNLOCK with advisory locks — the
/// redirector won't write without them (<see cref="WebDavLocks"/>).</para>
///
/// <para><b>Who can reach it.</b> The listener binds to 127.0.0.1 only, with exclusive
/// address use so no other process can bind the same port, and rejects connections that
/// don't come from a loopback address. That still leaves every local process — another
/// user's included — and web pages (through DNS rebinding) able to connect. Against those:
/// <list type="bullet">
/// <item>Everything lives under an unguessable first path segment,
/// <c>FileFluss-&lt;128 random bits&gt;</c> (<see cref="Secret"/>). Without it every request
/// is answered 404 (OPTIONS, and an empty listing of "/", excepted: the redirector probes
/// the server root before it maps a share). The drive maps as
/// <c>\\127.0.0.1@port\FileFluss-…</c>, so the secret doubles as the share name.</item>
/// <item>Requests whose <c>Host</c> isn't 127.0.0.1 / localhost are refused, which defeats
/// DNS rebinding (a page on evil.example resolving to 127.0.0.1 still sends its own Host).</item>
/// </list>
/// HTTP authentication was the alternative and is the worse one here: the redirector only
/// sends Basic credentials over HTTPS (<c>BasicAuthLevel</c> = 1 by default) and Digest/NTLM
/// would mean storing a password or impersonating the user. Since the redirector runs as
/// LocalService, the connecting process can't be used to identify the user either. The
/// secret is visible to the user's own processes (<c>net use</c> lists it); drive mappings
/// of other logon sessions are not.</para>
///
/// <para>Unlike the macOS server, connections are kept alive between requests: the
/// redirector fires bursts of PROPFINDs and a fresh TCP connection for each one costs.</para>
/// </summary>
public sealed class WebDavServer : IAsyncDisposable
{
    public const string LogCategory = "Mount";

    /// <summary>The fixed start of every mount secret, so leftovers of a crashed run can be recognised.</summary>
    public const string SecretPrefix = "FileFluss-";

    /// <summary>XML request bodies (PROPFIND, PROPPATCH, LOCK) larger than this are refused.</summary>
    private const int MaxXmlBodyBytes = 1024 * 1024;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QuotaLifetime = TimeSpan.FromMinutes(1);

    private readonly FileProvider _provider;
    private readonly WebDavServerOptions _options;
    private readonly string _mountRoot;
    private readonly WebDavMetadataCache _metadata;
    private readonly WebDavLocks _locks = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TcpClient, Task> _connections = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly bool _ownsTempDirectory;
    private readonly SemaphoreSlim _quotaGate = new(1, 1);
    private volatile QuotaSnapshot? _quota;
    private CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public WebDavServer(FileProvider provider, WebDavServerOptions? options = null)
    {
        if (provider.IsLocal)
        {
            throw new ArgumentException("Only cloud providers (POSIX paths) can be mounted.", nameof(provider));
        }

        _provider = provider;
        _options = options ?? new WebDavServerOptions();
        _mountRoot = CloudPath.Normalize(_options.MountRoot);
        Secret = _options.Secret ?? NewSecret();
        if (Secret.Length == 0 || Secret.Contains('/') || Secret.Contains('\\'))
        {
            throw new ArgumentException("The secret must be a single path segment.", nameof(options));
        }

        _ownsTempDirectory = _options.TempDirectory is null;
        TempDirectory = _options.TempDirectory ?? StagingLocation.StagingDirectory("webdav");
        Directory.CreateDirectory(TempDirectory);
        _metadata = new WebDavMetadataCache(provider.NameComparer, _options.MetadataLifetime);
    }

    /// <summary>
    /// Raised (on a pool thread) when the provider refuses a request because its sign-in
    /// expired, so the app can show its "sign in again" state. Explorer only sees 403.
    /// </summary>
    public event EventHandler<CloudProviderException>? ProviderNeedsReauthentication;

    /// <summary>The bound port; 0 before <see cref="StartAsync"/>.</summary>
    public int Port { get; private set; }

    /// <summary>The secret first path segment (also the share name in the UNC path).</summary>
    public string Secret { get; }

    public string TempDirectory { get; }

    public string MountRoot => _mountRoot;

    /// <summary><c>http://127.0.0.1:port/FileFluss-…/</c> — the URL WebDAV clients use.</summary>
    public Uri BaseUri => new(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/{Secret}/"));

    /// <summary><c>\\127.0.0.1@port\FileFluss-…</c> — the same location as the redirector's UNC path.</summary>
    public string UncPath => string.Create(CultureInfo.InvariantCulture, $@"\\127.0.0.1@{Port}\{Secret}");

    /// <summary>A fresh secret: the prefix plus 128 random bits as lower-case hex.</summary>
    public static string NewSecret() => SecretPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>True for a share name made by <see cref="NewSecret"/> (any case — the redirector may fold it).</summary>
    public static bool IsMountSecret(string? segment)
        => segment is { Length: 42 }
           && segment.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase)
           && segment[SecretPrefix.Length..].All(char.IsAsciiHexDigit);

    // MARK: - Lifecycle

    /// <summary>
    /// Starts listening on 127.0.0.1 and returns the bound port. 0 (the default) lets the OS
    /// pick a free one. Explicitly IPv4: the redirector connects to the literal 127.0.0.1 of
    /// the UNC path (macOS learned the same lesson the hard way — its listener defaulted to
    /// IPv6 and webdavfs_agent's IPv4 connections were refused).
    /// </summary>
    public Task<int> StartAsync(int preferredPort = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_listener is not null)
        {
            return Task.FromResult(Port);
        }

        if (_lifetime.IsCancellationRequested)
        {
            _lifetime = new CancellationTokenSource();
        }

        var listener = new TcpListener(IPAddress.Loopback, preferredPort);

        // Windows lets a second socket bind an already-bound port with SO_REUSEADDR unless
        // the first claims it exclusively — without this a local process could hijack the
        // port and receive the redirector's requests (and the secret in their paths).
        listener.ExclusiveAddressUse = true;
        listener.Start(backlog: 64);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _lifetime.Token), CancellationToken.None);
        SupportLogger.Shared.Log(string.Create(CultureInfo.InvariantCulture, $"WebDAV server listening on 127.0.0.1:{Port} for {_mountRoot}"), LogCategory);
        return Task.FromResult(Port);
    }

    /// <summary>Stops listening, drops open connections and removes the server's temp files.</summary>
    public async Task StopAsync()
    {
        if (_listener is not { } listener)
        {
            return;
        }

        _listener = null;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        foreach (var client in _connections.Keys)
        {
            client.Dispose();
        }

        var pending = _connections.Values.ToList();
        if (_acceptLoop is { } loop)
        {
            pending.Add(loop);
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }

        if (_ownsTempDirectory)
        {
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        _metadata.Clear();
        SupportLogger.Shared.Log(string.Create(CultureInfo.InvariantCulture, $"WebDAV server on port {Port} stopped"), LogCategory);
        Port = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _quotaGate.Dispose();
    }

    // MARK: - Connections

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            // Defence in depth: the socket is bound to 127.0.0.1, but never serve anything
            // that didn't arrive over loopback.
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            {
                client.Dispose();
                continue;
            }

            client.NoDelay = true;
            var task = ServeConnectionAsync(client, cancellationToken);
            _connections[client] = task;
            _ = task.ContinueWith(_ => _connections.TryRemove(client, out Task? _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var connection = new HttpConnection(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    DavRequestHead? head;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        idle.CancelAfter(IdleTimeout);
                        try
                        {
                            head = await connection.ReadHeadAsync(idle.Token).ConfigureAwait(false);
                        }
                        catch (HttpProtocolException e)
                        {
                            await connection.WriteResponseAsync(DavResponse.Empty(e.Status), headOnly: false, keepAlive: false, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    }

                    if (head is null || !await ServeRequestAsync(connection, head, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client went away or the server is stopping; the redirector retries.
            }
        }
    }

    /// <summary>Reads the body, dispatches, writes the answer. False when the connection must close.</summary>
    private async Task<bool> ServeRequestAsync(HttpConnection connection, DavRequestHead head, CancellationToken cancellationToken)
    {
        DavRequest? request = null;
        DavResponse response;
        try
        {
            request = await ReadRequestAsync(connection, head, cancellationToken).ConfigureAwait(false);
            response = request is null
                ? NotFoundAndClose()
                : await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpProtocolException e)
        {
            response = DavResponse.Empty(e.Status);
            response.ForceClose = true;
        }

        var keepAlive = head.WantsKeepAlive && !response.ForceClose;
        try
        {
            await connection.WriteResponseAsync(response, headOnly: head.Method == "HEAD", keepAlive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (response.FileBody is { } file)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }

            if (request?.BodyFile is { } bodyFile)
            {
                TryDelete(bodyFile);
            }
        }

        return keepAlive;
    }

    private static DavResponse NotFoundAndClose()
    {
        var response = DavResponse.Empty(404);
        response.ForceClose = true;
        return response;
    }

    /// <summary>
    /// Validates Host and path, then reads the body: PUT bodies stream to a temp file (never
    /// held in memory), anything else must be small XML. Null when the request is outside
    /// the mount and carries a body — answered 404 without reading it, so a stranger can't
    /// make the server write to disk.
    /// </summary>
    private async Task<DavRequest?> ReadRequestAsync(HttpConnection connection, DavRequestHead head, CancellationToken cancellationToken)
    {
        if (!IsLoopbackHost(head.Header("Host")))
        {
            throw new HttpProtocolException(403, "Host is not loopback");
        }

        var path = DecodeTarget(head.RawTarget) ?? throw new HttpProtocolException(400, "Bad request target");
        if (!head.HasBody)
        {
            return new DavRequest { Head = head, Path = path };
        }

        if (Classify(path) is null)
        {
            return null;
        }

        if (head.Header("Expect") is { } expect && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await connection.WriteContinueAsync(cancellationToken).ConfigureAwait(false);
        }

        if (head.Method == "PUT")
        {
            var file = NewTempFile();
            try
            {
                await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous))
                {
                    await connection.ReadBodyAsync(head, output, long.MaxValue, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                TryDelete(file);
                throw;
            }

            return new DavRequest { Head = head, Path = path, BodyFile = file };
        }

        using var buffer = new MemoryStream();
        await connection.ReadBodyAsync(head, buffer, MaxXmlBodyBytes, cancellationToken).ConfigureAwait(false);
        return new DavRequest { Head = head, Path = path, Body = buffer.ToArray() };
    }

    // MARK: - Dispatch

    /// <summary>Where a request path points: inside the mount (with the path below the secret), or null.</summary>
    private sealed record Target(string SecretSegment, string Relative, string ProviderPath)
    {
        public string Name => CloudPath.Name(ProviderPath);
    }

    private async Task<DavResponse> DispatchAsync(DavRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "OPTIONS")
        {
            return Options();
        }

        var target = Classify(request.Path);
        if (target is null)
        {
            // The redirector probes the server root before mapping \\127.0.0.1@port\share;
            // answer it with an empty collection that gives nothing away.
            return request.Method == "PROPFIND" && request.Path == "/" ? ServerRootPropfind(request) : DavResponse.Empty(404);
        }

        try
        {
            return request.Method switch
            {
                "PROPFIND" => await PropfindAsync(request, target, cancellationToken).ConfigureAwait(false),
                "PROPPATCH" => await ProppatchAsync(request, target, cancellationToken).ConfigureAwait(false),
                "GET" or "HEAD" => await GetAsync(request, target, cancellationToken).ConfigureAwait(false),
                "PUT" => await PutAsync(request, target, cancellationToken).ConfigureAwait(false),
                "MKCOL" => await MkcolAsync(request, target, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteAsync(target, cancellationToken).ConfigureAwait(false),
                "MOVE" => await MoveOrCopyAsync(request, target, move: true, cancellationToken).ConfigureAwait(false),
                "COPY" => await MoveOrCopyAsync(request, target, move: false, cancellationToken).ConfigureAwait(false),
                "LOCK" => await LockAsync(request, target, cancellationToken).ConfigureAwait(false),
                "UNLOCK" => Unlock(request),
                _ => DavResponse.Empty(501),
            };
        }
        catch (XmlException)
        {
            return DavResponse.Empty(400);
        }
        catch (Exception e) when (e is not HttpProtocolException)
        {
            return MapError(e, request.Method, target.ProviderPath);
        }
    }

    private static DavResponse Options()
        // The redirector reads the DAV class list (2 = locking, which it needs to write) and
        // MS-Author-Via before mapping; macOS added Accept-Ranges and a Server header for
        // webdavfs_agent's benefit, which do no harm here.
        => DavResponse.Empty(200)
            .With("DAV", "1, 2")
            .With("MS-Author-Via", "DAV")
            .With("Allow", "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK")
            .With("Accept-Ranges", "bytes")
            .With("Cache-Control", "no-cache")
            .With("Content-Length", "0");

    private DavResponse ServerRootPropfind(DavRequest request)
    {
        var propfind = WebDavXml.ParsePropfind(request.Body);
        var root = new DavResource("/", "/", "FileFluss", true, 0, _started, _started, false, string.Empty);
        return DavResponse.Xml(207, WebDavXml.RenderPropfind([root], propfind, new DavResponseContext(null, null, _ => [])));
    }

    // MARK: - PROPFIND

    private async Task<DavResponse> PropfindAsync(DavRequest request, Target target, CancellationToken cancellationToken)
    {
        var propfind = WebDavXml.ParsePropfind(request.Body);
        var depth = (request.Header("Depth") ?? "1").Trim();
        var resources = new List<DavResource>();

        // File-manager metadata (desktop.ini, Thumbs.db, .DS_Store, ._*) is answered from
        // the sidecar store, never from the cloud — macOS: without this every drag fired
        // hundreds of PROPFINDs at the provider's API.
        if (IsSidecarName(target.Name))
        {
            if (_options.Sidecar?.Metadata(target.ProviderPath) is not { } meta)
            {
                return DavResponse.Empty(404);
            }

            resources.Add(new DavResource(Href(target, false), target.ProviderPath, target.Name, false, meta.Size, meta.Modified, meta.Modified, true, ContentType(target.Name)));
            return DavResponse.Xml(207, WebDavXml.RenderPropfind(resources, propfind, await ContextAsync(propfind, cancellationToken).ConfigureAwait(false)));
        }

        if (await FindAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false) is not { } item)
        {
            return DavResponse.Empty(404);
        }

        resources.Add(Resource(item, target.ProviderPath, Href(target, item.IsDirectory), IsMountRoot(target) ? _options.DisplayName : item.Name));

        // Depth 1, and "infinity" treated as 1 (as on macOS) — nobody needs a whole cloud
        // tree in one response.
        if (depth != "0" && item.IsDirectory)
        {
            // macOS answered a failed listing with just the folder itself, which Finder
            // tolerated. Explorer would show that as an empty folder — indistinguishable from
            // data loss — so the failure is reported instead.
            foreach (var child in await ChildrenAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false))
            {
                if (IsSidecarName(child.Name))
                {
                    continue;
                }

                var childPath = CloudPath.Combine(target.ProviderPath, child.Name);
                var childTarget = target with { Relative = CloudPath.Combine(target.Relative, child.Name), ProviderPath = childPath };
                resources.Add(Resource(child, childPath, Href(childTarget, child.IsDirectory), child.Name));
            }
        }

        return DavResponse.Xml(207, WebDavXml.RenderPropfind(resources, propfind, await ContextAsync(propfind, cancellationToken).ConfigureAwait(false)));
    }

    private DavResource Resource(CloudFileItem item, string providerPath, string href, string displayName)
    {
        var modified = item.HasKnownModificationDate ? item.ModificationDate : _started;
        var created = item.CreationDate > CloudFileItem.UnknownDate ? item.CreationDate : modified;
        var hidden = item.IsHidden || CloudFileItem.IsDotHidden(item.Name);
        return new DavResource(href, providerPath, displayName, item.IsDirectory, item.IsDirectory ? 0 : Math.Max(0, item.Size), modified, created, hidden, ContentType(item.Name));
    }

    private async Task<DavResponseContext> ContextAsync(PropfindRequest propfind, CancellationToken cancellationToken)
    {
        var wantsQuota = propfind.Kind == PropfindKind.Prop
                         && propfind.Properties.Any(p => p.Namespace == DavProperty.Dav && p.Name is "quota-available-bytes" or "quota-used-bytes");
        var (used, free) = wantsQuota ? await QuotaAsync(cancellationToken).ConfigureAwait(false) : (null, null);
        return new DavResponseContext(used, free, _locks.For);
    }

    /// <summary>
    /// Account usage for <c>quota-*</c> — Explorer asks on every free-space query of the
    /// drive, so the answer is kept for a minute. Unknown → those properties answer 404 and
    /// Explorer shows the drive without a size bar.
    /// </summary>
    private async Task<(long? Used, long? Free)> QuotaAsync(CancellationToken cancellationToken)
    {
        if (_quota is { } cached && DateTimeOffset.UtcNow - cached.Fetched < QuotaLifetime)
        {
            return (cached.Used, cached.Free);
        }

        await _quotaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_quota is { } again && DateTimeOffset.UtcNow - again.Fetched < QuotaLifetime)
            {
                return (again.Used, again.Free);
            }

            long? used = null;
            long? free = null;
            try
            {
                if (_provider is CloudProvider cloud)
                {
                    var quota = await cloud.StorageQuotaAsync(cancellationToken).ConfigureAwait(false);
                    used = quota?.UsedBytes;
                    free = quota?.FreeBytes;
                }
                else
                {
                    free = await _provider.AvailableSpaceAsync(_mountRoot, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is CloudProviderException or HttpRequestException or IOException)
            {
            }

            _quota = new QuotaSnapshot(DateTimeOffset.UtcNow, used, free);
            return (used, free);
        }
        finally
        {
            _quotaGate.Release();
        }
    }

    private sealed record QuotaSnapshot(DateTimeOffset Fetched, long? Used, long? Free);

    // MARK: - PROPPATCH

    /// <summary>
    /// After writing a file Explorer sets its Win32 times (and attributes) with PROPPATCH.
    /// macOS answered every PROPPATCH with success without looking — Finder failed whole
    /// drags with -43 on a 501. Here the modification time is also handed to the provider
    /// (best-effort, like a copy in the app), so a file copied onto the drive keeps its date
    /// where the cloud allows it. Everything else is accepted and dropped.
    /// </summary>
    private async Task<DavResponse> ProppatchAsync(DavRequest request, Target target, CancellationToken cancellationToken)
    {
        var (set, remove) = WebDavXml.ParseProppatch(request.Body);
        if (!IsSidecarName(target.Name))
        {
            var modified = set.FirstOrDefault(p => p.Property == DavProperty.Z("Win32LastModifiedTime"));
            if (modified.Value is null)
            {
                modified = set.FirstOrDefault(p => p.Property == DavProperty.D("getlastmodified"));
            }

            if (modified.Value is { } text && WebDavXml.ParseDate(text) is { } date)
            {
                try
                {
                    await _provider.SetModificationDateAsync(target.ProviderPath, date, cancellationToken).ConfigureAwait(false);
                    _metadata.InvalidateParentOf(target.ProviderPath);
                    _options.ReadCache?.Invalidate(target.ProviderPath);
                }
                catch (CloudProviderException e) when (e.Kind != CloudErrorKind.Cancelled)
                {
                    // Best-effort, as FileProvider.SetModificationDateAsync says.
                }
            }
        }

        var href = Href(target, request.Path.EndsWith('/'));
        return DavResponse.Xml(207, WebDavXml.RenderProppatch(href, set.Select(p => p.Property).Concat(remove)));
    }

    // MARK: - GET / HEAD

    private async Task<DavResponse> GetAsync(DavRequest request, Target target, CancellationToken cancellationToken)
    {
        var headOnly = request.Method == "HEAD";
        if (IsSidecarName(target.Name))
        {
            if (_options.Sidecar is not { } sidecar || sidecar.Metadata(target.ProviderPath) is not { } meta)
            {
                return DavResponse.Empty(404);
            }

            if (headOnly)
            {
                return FileHeaders(DavResponse.Empty(200), target.Name, meta.Modified, meta.Size);
            }

            var sidecarFile = sidecar.Open(target.ProviderPath);
            return sidecarFile is null ? DavResponse.Empty(404) : RangeResponse(request, sidecarFile, target.Name, meta.Modified, null);
        }

        if (await FindAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false) is not { } item)
        {
            return DavResponse.Empty(404);
        }

        if (item.IsDirectory)
        {
            // A GET on a collection isn't meaningful; the redirector never sends one.
            return DavResponse.Empty(405);
        }

        var modified = item.HasKnownModificationDate ? item.ModificationDate : _started;
        if (headOnly)
        {
            return FileHeaders(DavResponse.Empty(200), item.Name, modified, item.Size);
        }

        var stream = await OpenContentAsync(target.ProviderPath, item, cancellationToken).ConfigureAwait(false);
        return RangeResponse(request, stream, item.Name, modified, Resource(item, target.ProviderPath, string.Empty, item.Name));
    }

    /// <summary>
    /// The file's bytes as an open stream: from the read cache when (path, mtime + size)
    /// matches, otherwise downloaded once — concurrent GETs of the same version share one
    /// download — and stored. Files the cache can't hold are downloaded to a temp file
    /// that deletes itself when the response is done.
    /// </summary>
    private async Task<FileStream> OpenContentAsync(string providerPath, CloudFileItem item, CancellationToken cancellationToken)
    {
        var cache = _options.ReadCache;
        var version = string.Create(CultureInfo.InvariantCulture, $"{item.ModificationDate.ToUnixTimeSeconds()}-{item.Size}");
        if (cache?.TryOpen(providerPath, version) is { } hit)
        {
            return hit;
        }

        if (cache is not null && item.Size <= cache.MaxBytes)
        {
            var key = providerPath + "\0" + version;
            var flight = _downloads.GetOrAdd(key, k => new Lazy<Task>(() => DownloadIntoCacheAsync(k, cache, providerPath, version)));
            await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (cache.TryOpen(providerPath, version) is { } stored)
            {
                return stored;
            }
        }

        var temp = NewTempFile();
        try
        {
            await _provider.DownloadFileAsync(providerPath, temp, null, cancellationToken).ConfigureAwait(false);
            return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// One shared download, tied to the server's lifetime rather than to the first
    /// requester (who may hang up while others still wait). It leaves the in-flight table
    /// itself, so a failure is never handed to a later request.
    /// </summary>
    private async Task DownloadIntoCacheAsync(string key, WebDavReadCache cache, string providerPath, string version)
    {
        await Task.Yield();
        var temp = NewTempFile();
        try
        {
            await _provider.DownloadFileAsync(providerPath, temp, null, _lifetime.Token).ConfigureAwait(false);
            cache.Store(providerPath, version, temp);
        }
        finally
        {
            TryDelete(temp);
            _downloads.TryRemove(key, out _);
        }
    }

    /// <summary>A 200 with the whole stream, or a 206 for one satisfiable byte range; multi-range requests get the whole file.</summary>
    private static DavResponse RangeResponse(DavRequest request, FileStream stream, string name, DateTimeOffset modified, DavResource? resource)
    {
        var length = stream.Length;
        DavResponse response;
        if (ParseRange(request.Header("Range"), length) is { } range)
        {
            if (range.Start >= length)
            {
                stream.Dispose();
                return DavResponse.Empty(416).With("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes */{length}"));
            }

            response = new DavResponse(206) { FileBody = stream, FileOffset = range.Start, FileLength = range.End - range.Start + 1 };
            response.With("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{length}"));
        }
        else
        {
            response = new DavResponse(200) { FileBody = stream, FileOffset = 0, FileLength = length };
        }

        // Length from the bytes actually on disk: the provider's reported size can be off
        // (macOS re-stats for the same reason).
        response.With("Content-Length", response.FileLength.ToString(CultureInfo.InvariantCulture));
        FileHeaders(response, name, modified, null);
        if (resource is not null)
        {
            response.With("ETag", WebDavXml.ETag(resource));
        }

        return response;
    }

    private static DavResponse FileHeaders(DavResponse response, string name, DateTimeOffset modified, long? contentLength)
    {
        if (contentLength is { } length)
        {
            response.With("Content-Length", Math.Max(0, length).ToString(CultureInfo.InvariantCulture));
        }

        return response
            .With("Content-Type", ContentType(name))
            .With("Last-Modified", WebDavXml.HttpDate(modified))
            .With("Accept-Ranges", "bytes");
    }

    /// <summary><c>bytes=a-b</c>, <c>bytes=a-</c> or <c>bytes=-n</c>, clamped to the file; null for none or several ranges.</summary>
    internal static (long Start, long End)? ParseRange(string? header, long length)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = header["bytes=".Length..].Trim();
        if (spec.Contains(','))
        {
            return null;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix == 0)
            {
                return null;
            }

            return length == 0 ? null : (Math.Max(0, length - suffix), length - 1);
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }

        var end = length - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
            {
                return null;
            }

            end = Math.Min(end, length - 1);
        }

        return (start, end);
    }

    // MARK: - PUT

    /// <summary>
    /// Uploads the body. An empty body is a real request here: the redirector creates every
    /// new file with a zero-length PUT before writing its content (macOS refused a missing
    /// body with 411, which would break that).
    /// </summary>
    private async Task<DavResponse> PutAsync(DavRequest request, Target target, CancellationToken cancellationToken)
    {
        if (IsMountRoot(target))
        {
            return DavResponse.Empty(405);
        }

        if (IsSidecarName(target.Name))
        {
            // Kept locally (if there is a store) so view state survives a revisit; the cloud
            // stays clean either way.
            _options.Sidecar?.Put(target.ProviderPath, request.BodyFile);
            return DavResponse.Empty(201);
        }

        var parent = CloudPath.Parent(target.ProviderPath);
        if (await FindAsync(parent, cancellationToken).ConfigureAwait(false) is not { IsDirectory: true })
        {
            return DavResponse.Empty(409);
        }

        var existing = await FindAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false);
        if (existing is { IsDirectory: true })
        {
            return DavResponse.Empty(405);
        }

        var body = request.BodyFile;
        if (body is null)
        {
            body = NewTempFile();
            await File.WriteAllBytesAsync(body, [], cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (existing is null || _options.UploadReplacesInPlace)
            {
                await _provider.UploadFileAsync(body, target.ProviderPath, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReplaceViaSiblingAsync(body, target.ProviderPath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (request.BodyFile is null)
            {
                TryDelete(body);
            }

            Invalidate(target.ProviderPath);
        }

        SupportLogger.Shared.Log($"Mount: wrote {target.ProviderPath}", LogCategory);
        return DavResponse.Empty(existing is null ? 201 : 204);
    }

    /// <summary>
    /// Overwrite without a window in which the file is gone: upload under a hidden sibling
    /// name, delete the original, rename the sibling into place. If the upload fails the
    /// original is untouched; if the final rename fails the new content survives under the
    /// sibling's name.
    /// </summary>
    private async Task ReplaceViaSiblingAsync(string localFile, string providerPath, CancellationToken cancellationToken)
    {
        var sibling = CloudPath.Combine(CloudPath.Parent(providerPath), ".~filefluss-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        await _provider.UploadFileAsync(localFile, sibling, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await _provider.DeleteItemAsync(providerPath, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
        }
        catch
        {
            await TryDeleteRemoteAsync(sibling).ConfigureAwait(false);
            throw;
        }

        await _provider.RenameItemAsync(sibling, CloudPath.Name(providerPath), cancellationToken).ConfigureAwait(false);
    }

    // MARK: - MKCOL / DELETE

    private async Task<DavResponse> MkcolAsync(DavRequest request, Target target, CancellationToken cancellationToken)
    {
        if (request.Body.Length > 0)
        {
            return DavResponse.Empty(415);
        }

        if (await FindAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false) is not null)
        {
            // RFC 4918: MKCOL on an existing resource is 405.
            return DavResponse.Empty(405);
        }

        if (await FindAsync(CloudPath.Parent(target.ProviderPath), cancellationToken).ConfigureAwait(false) is not { IsDirectory: true })
        {
            return DavResponse.Empty(409);
        }

        try
        {
            await _provider.CreateDirectoryAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Invalidate(target.ProviderPath);
        }

        SupportLogger.Shared.Log($"Mount: created folder {target.ProviderPath}", LogCategory);
        return DavResponse.Empty(201);
    }

    private async Task<DavResponse> DeleteAsync(Target target, CancellationToken cancellationToken)
    {
        if (IsMountRoot(target))
        {
            return DavResponse.Empty(403);
        }

        if (IsSidecarName(target.Name))
        {
            _options.Sidecar?.Delete(target.ProviderPath);
            return DavResponse.Empty(204);
        }

        try
        {
            await _provider.DeleteItemAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Invalidate(target.ProviderPath);
            _locks.ReleaseTree(target.ProviderPath);
        }

        SupportLogger.Shared.Log($"Mount: deleted {target.ProviderPath}", LogCategory);
        return DavResponse.Empty(204);
    }

    // MARK: - MOVE / COPY

    private async Task<DavResponse> MoveOrCopyAsync(DavRequest request, Target source, bool move, CancellationToken cancellationToken)
    {
        if (request.Header("Destination") is not { } destinationHeader)
        {
            return DavResponse.Empty(400);
        }

        if (DestinationPath(destinationHeader) is not { } destinationPath)
        {
            // Another server (or port) — RFC 4918 says 502 for a cross-server COPY/MOVE.
            return DavResponse.Empty(502);
        }

        if (Classify(destinationPath) is not { } destination)
        {
            return DavResponse.Empty(403);
        }

        if (IsMountRoot(source) || IsMountRoot(destination))
        {
            return DavResponse.Empty(403);
        }

        if (destination.ProviderPath == source.ProviderPath || CloudPath.IsWithin(destination.ProviderPath, source.ProviderPath))
        {
            return DavResponse.Empty(403);
        }

        if (IsSidecarName(source.Name) || IsSidecarName(destination.Name))
        {
            // Metadata files never travel to the cloud, and never come back from it.
            return DavResponse.Empty(201);
        }

        if (await FindAsync(source.ProviderPath, cancellationToken).ConfigureAwait(false) is not { } item)
        {
            return DavResponse.Empty(404);
        }

        // "a.txt" → "A.txt" on a store that folds case is a rename of the same item; looking
        // up the destination would find the source and "overwrite" would delete it.
        if (await FindAsync(CloudPath.Parent(destination.ProviderPath), cancellationToken).ConfigureAwait(false) is not { IsDirectory: true })
        {
            return DavResponse.Empty(409);
        }

        var caseOnlyRename = move && _provider.NameComparer.Equals(destination.ProviderPath, source.ProviderPath);
        var existing = caseOnlyRename ? null : await FindAsync(destination.ProviderPath, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (string.Equals((request.Header("Overwrite") ?? "T").Trim(), "F", StringComparison.OrdinalIgnoreCase))
            {
                return DavResponse.Empty(412);
            }

            // RFC 4918: with Overwrite: T the destination is deleted before the move/copy.
            // Explorer only sends that after the user confirmed "Replace the file".
            await _provider.DeleteItemAsync(destination.ProviderPath, cancellationToken).ConfigureAwait(false);
            Invalidate(destination.ProviderPath);
        }

        try
        {
            if (move)
            {
                await MoveAsync(item, source.ProviderPath, destination.ProviderPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var deep = !string.Equals((request.Header("Depth") ?? "infinity").Trim(), "0", StringComparison.Ordinal);
                try
                {
                    await _provider.CopyItemAsync(source.ProviderPath, destination.ProviderPath, cancellationToken).ConfigureAwait(false);
                }
                catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotImplemented)
                {
                    // macOS answered 502 here so Finder would copy by itself; the redirector
                    // doesn't, so the copy happens through this machine instead.
                    await CopyTreeAsync(item, source.ProviderPath, destination.ProviderPath, deep, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Invalidate(destination.ProviderPath);
            if (move)
            {
                Invalidate(source.ProviderPath);
                _locks.ReleaseTree(source.ProviderPath);
            }
        }

        SupportLogger.Shared.Log($"Mount: {(move ? "moved" : "copied")} {source.ProviderPath} → {destination.ProviderPath}", LogCategory);
        return DavResponse.Empty(existing is null ? 201 : 204);
    }

    private async Task MoveAsync(CloudFileItem item, string from, string to, CancellationToken cancellationToken)
    {
        try
        {
            if (CloudPath.Parent(from) == CloudPath.Parent(to))
            {
                // A pure rename within one folder.
                await _provider.RenameItemAsync(from, CloudPath.Name(to), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _provider.MoveItemAsync(from, to, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotImplemented)
        {
            // No server-side move. macOS learned that answering 502 makes the client give up
            // instead of copying itself, so emulate it: copy, then delete the source. Unlike
            // macOS (files only) folders are copied recursively too.
            await CopyTreeAsync(item, from, to, deep: true, cancellationToken).ConfigureAwait(false);
            await _provider.DeleteItemAsync(from, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies through this machine: download + upload per file, folders recreated.</summary>
    private async Task CopyTreeAsync(CloudFileItem item, string from, string to, bool deep, CancellationToken cancellationToken)
    {
        if (!item.IsDirectory)
        {
            var temp = NewTempFile();
            try
            {
                await _provider.DownloadFileAsync(from, temp, null, cancellationToken).ConfigureAwait(false);
                await _provider.UploadFileAsync(temp, to, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(temp);
            }

            return;
        }

        await _provider.CreateDirectoryAsync(to, cancellationToken).ConfigureAwait(false);
        if (!deep)
        {
            return;
        }

        foreach (var child in await _provider.ListDirectoryAsync(from, cancellationToken).ConfigureAwait(false))
        {
            if (IsSidecarName(child.Name))
            {
                continue;
            }

            await CopyTreeAsync(child, CloudPath.Combine(from, child.Name), CloudPath.Combine(to, child.Name), deep, cancellationToken).ConfigureAwait(false);
        }
    }

    // MARK: - LOCK / UNLOCK

    private async Task<DavResponse> LockAsync(DavRequest request, Target target, CancellationToken cancellationToken)
    {
        var timeout = WebDavLocks.ParseTimeout(request.Header("Timeout"));
        if (request.Body.Length == 0)
        {
            // A refresh: no body, the lock named in the If header.
            foreach (var token in WebDavLocks.TokensInIfHeader(request.Header("If")))
            {
                if (_locks.Refresh(token, timeout) is { } refreshed)
                {
                    return DavResponse.Xml(200, WebDavXml.RenderLock(refreshed)).With("Lock-Token", "<" + refreshed.Token + ">");
                }
            }

            return DavResponse.Empty(412);
        }

        var owner = WebDavXml.ParseLockOwner(request.Body);
        var exclusive = !Encoding.UTF8.GetString(request.Body).Contains("shared", StringComparison.Ordinal);
        var depth = (request.Header("Depth") ?? "infinity").Trim() == "0" ? "0" : "infinity";
        var status = 200;

        if (IsSidecarName(target.Name))
        {
            if (_options.Sidecar is { } sidecar && !sidecar.Exists(target.ProviderPath))
            {
                sidecar.Put(target.ProviderPath, null);
                status = 201;
            }
        }
        else if (!IsMountRoot(target) && await FindAsync(target.ProviderPath, cancellationToken).ConfigureAwait(false) is null)
        {
            // RFC 4918 §9.10.4: LOCK on an unmapped URL creates an empty resource (Office
            // locks a new document's name before its first PUT).
            if (await FindAsync(CloudPath.Parent(target.ProviderPath), cancellationToken).ConfigureAwait(false) is not { IsDirectory: true })
            {
                return DavResponse.Empty(409);
            }

            var empty = NewTempFile();
            try
            {
                await File.WriteAllBytesAsync(empty, [], cancellationToken).ConfigureAwait(false);
                await _provider.UploadFileAsync(empty, target.ProviderPath, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(empty);
                Invalidate(target.ProviderPath);
            }

            status = 201;
        }

        var granted = _locks.Grant(target.ProviderPath, exclusive, depth, owner, timeout);
        return DavResponse.Xml(status, WebDavXml.RenderLock(granted)).With("Lock-Token", "<" + granted.Token + ">");
    }

    private DavResponse Unlock(DavRequest request)
    {
        var token = (request.Header("Lock-Token") ?? string.Empty).Trim().Trim('<', '>');
        _locks.Release(token);

        // 204 even for an unknown token, as macOS: a lock that expired or died with a
        // restart is gone either way, and an error here only alarms the client.
        return DavResponse.Empty(204);
    }

    // MARK: - Lookups and invalidation

    private async Task<CloudFileItem?> FindAsync(string providerPath, CancellationToken cancellationToken)
    {
        if (providerPath == _mountRoot)
        {
            return new CloudFileItem(_mountRoot, _options.DisplayName, _mountRoot, true, 0, _started);
        }

        if (_metadata.TryGet(providerPath, out var cached))
        {
            return cached;
        }

        try
        {
            return await _provider.GetFileMetadataAsync(providerPath, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task<IReadOnlyCollection<CloudFileItem>> ChildrenAsync(string folder, CancellationToken cancellationToken)
    {
        if (_metadata.Children(folder) is { } cached)
        {
            return cached;
        }

        var children = await _provider.ListDirectoryAsync(folder, cancellationToken).ConfigureAwait(false);
        _metadata.Store(folder, children);
        return children;
    }

    /// <summary>Forgets everything cached about <paramref name="providerPath"/>, beneath it and its folder.</summary>
    private void Invalidate(string providerPath)
    {
        _metadata.InvalidateTree(providerPath);
        _options.ReadCache?.InvalidateTree(providerPath);
        _quota = null;
    }

    private async Task TryDeleteRemoteAsync(string providerPath)
    {
        try
        {
            await _provider.DeleteItemAsync(providerPath, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CloudProviderException or OperationCanceledException or IOException or HttpRequestException)
        {
        }
    }

    // MARK: - Errors

    private DavResponse MapError(Exception error, string method, string providerPath)
    {
        if (error is CloudProviderException e)
        {
            if (e.Kind != CloudErrorKind.NotFound)
            {
                SupportLogger.Shared.Log($"Mount: {method} {providerPath} failed: {e.Message}", LogCategory, LogLevel.Error);
            }

            switch (e.Kind)
            {
                case CloudErrorKind.NotFound:
                    return DavResponse.Empty(404);
                case CloudErrorKind.Unauthorized or CloudErrorKind.NotAuthenticated or CloudErrorKind.InvalidCredentials:
                    // macOS answered 401. On Windows a 401 makes the redirector pop up a
                    // credential prompt for a server that has no credentials; 403 surfaces
                    // as "access denied", and the app shows its own sign-in banner.
                    ProviderNeedsReauthentication?.Invoke(this, e);
                    return DavResponse.Empty(403);
                case CloudErrorKind.QuotaExceeded:
                    return DavResponse.Empty(507);
                case CloudErrorKind.FileTooLarge:
                    return DavResponse.Empty(413);
                case CloudErrorKind.RateLimited:
                    return DavResponse.Empty(429).With("Retry-After", "5");
                case CloudErrorKind.NotImplemented:
                    return DavResponse.Empty(501);
                case CloudErrorKind.Cancelled:
                    return DavResponse.Empty(503);
                default:
                    return DavResponse.Empty(502);
            }
        }

        if (error is OperationCanceledException)
        {
            return DavResponse.Empty(503);
        }

        SupportLogger.Shared.Log($"Mount: {method} {providerPath} failed unexpectedly: {error.GetType().Name}: {error.Message}", LogCategory, LogLevel.Error);
        return DavResponse.Empty(500);
    }

    // MARK: - Paths

    /// <summary>
    /// Maps a decoded request path to the mount: the first segment must be the secret
    /// (compared in constant time, ignoring case — the redirector may fold the share name).
    /// </summary>
    private Target? Classify(string path)
    {
        var components = CloudPath.Components(path);
        if (components.Length == 0 || !SecretMatches(components[0]))
        {
            return null;
        }

        var relative = "/" + string.Join('/', components.Skip(1));
        var providerPath = components.Length == 1 ? _mountRoot : CloudPath.Combine(_mountRoot, relative);
        return new Target(components[0], relative, providerPath);
    }

    private bool SecretMatches(string segment)
        => segment.Length == Secret.Length
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(segment.ToLowerInvariant()),
               Encoding.UTF8.GetBytes(Secret.ToLowerInvariant()));

    private bool IsMountRoot(Target target) => target.ProviderPath == _mountRoot;

    /// <summary>
    /// The href for a target: the secret segment as the client sent it, each name
    /// percent-encoded, collections with a trailing slash.
    /// </summary>
    private static string Href(Target target, bool isCollection)
    {
        var builder = new StringBuilder("/").Append(EncodeSegment(target.SecretSegment));
        foreach (var component in CloudPath.Components(target.Relative))
        {
            builder.Append('/').Append(EncodeSegment(component));
        }

        if (isCollection)
        {
            builder.Append('/');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Percent-encodes one path segment: RFC 3986 unreserved characters and the sub-delims
    /// that are legal in a path stay literal (the redirector sends them that way, and
    /// matching its spelling keeps it from mistaking a folder's own entry for a child);
    /// everything else, non-ASCII as UTF-8, is escaped.
    /// </summary>
    internal static string EncodeSegment(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var b in Encoding.UTF8.GetBytes(segment))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*,;=:@".Contains(c)))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The decoded path of a request target (origin-form or absolute-form); null when it
    /// can't be one of ours — including any "." or ".." segment.
    /// </summary>
    internal static string? DecodeTarget(string rawTarget)
    {
        var target = rawTarget;
        if (target == "*")
        {
            return "/";
        }

        var scheme = target.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && target[..scheme].All(char.IsAsciiLetter))
        {
            var slash = target.IndexOf('/', scheme + 3);
            target = slash < 0 ? "/" : target[slash..];
        }

        var cut = target.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            target = target[..cut];
        }

        if (!target.StartsWith('/'))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".." || s.Contains('\0')))
        {
            return null;
        }

        var path = "/" + string.Join('/', segments);
        return decoded.EndsWith('/') && path != "/" ? path + "/" : path;
    }

    /// <summary>The decoded path of a Destination header, or null when it names another server.</summary>
    private string? DestinationPath(string header)
    {
        if (Uri.TryCreate(header, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (!IsLoopbackHost(uri.Authority) || uri.Port != Port)
            {
                return null;
            }

            return DecodeTarget(uri.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped));
        }

        return DecodeTarget(header);
    }

    /// <summary>127.0.0.1, localhost or [::1], with or without a port; a missing Host (HTTP/1.0) passes.</summary>
    internal static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }

        var name = host.Trim();
        if (name.StartsWith('['))
        {
            var close = name.IndexOf(']');
            name = close < 0 ? name : name[..(close + 1)];
        }
        else if (name.IndexOf(':') is var colon and >= 0)
        {
            name = name[..colon];
        }

        return name is "127.0.0.1" or "[::1]" || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    // MARK: - Sidecar files

    /// <summary>
    /// Metadata a file manager writes for itself, which has no business in the user's cloud:
    /// Explorer's <c>desktop.ini</c> (folder customisation) and thumbnail caches, and — as on
    /// macOS — Finder's <c>.DS_Store</c>, AppleDouble <c>._*</c> forks and volume folders
    /// (a Mac user's cloud is full of them). Matched ignoring case, as NTFS would. A real
    /// <c>desktop.ini</c> that already lives in the cloud is hidden from the drive as a
    /// consequence.
    /// </summary>
    internal static bool IsSidecarName(string name)
        => name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("._", StringComparison.Ordinal)
           || name is ".Spotlight-V100" or ".Trashes" or ".fseventsd"
           || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
           || name.Equals("ehthumbs.db", StringComparison.OrdinalIgnoreCase)
           || name.Equals("ehthumbs_vista.db", StringComparison.OrdinalIgnoreCase);

    // MARK: - Helpers

    private string NewTempFile()
    {
        Directory.CreateDirectory(TempDirectory);
        return Path.Combine(TempDirectory, "dav-" + Guid.NewGuid().ToString("N"));
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

    internal static string ContentType(string name)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
        return extension switch
        {
            "txt" or "md" or "log" or "csv" => "text/plain; charset=utf-8",
            "html" or "htm" => "text/html; charset=utf-8",
            "json" => "application/json",
            "xml" => "application/xml",
            "pdf" => "application/pdf",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "heic" => "image/heic",
            "mp4" or "m4v" => "video/mp4",
            "mov" => "video/quicktime",
            "mp3" => "audio/mpeg",
            "m4a" => "audio/mp4",
            "zip" => "application/zip",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };
    }
}
