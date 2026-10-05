// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Mount;
using FileFluss.Core.Providers;
using FileFluss.Core.Settings;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Services;

/// <summary>An account currently shown as a drive in File Explorer.</summary>
/// <param name="DriveLetter">"X:".</param>
/// <param name="RootPath">The provider folder the drive's root shows.</param>
/// <param name="Url">The loopback WebDAV URL (contains the mount secret — don't log or show it).</param>
/// <param name="UncPath"><c>\\127.0.0.1@port\FileFluss-…</c>, what the drive letter is mapped to.</param>
/// <param name="FileSizeLimitBytes">WebClient's per-file cap (50 MB by default); null when unknown.</param>
public sealed record MountInfo(
    Guid AccountId,
    string DisplayName,
    string DriveLetter,
    string RootPath,
    int Port,
    Uri Url,
    string UncPath,
    long? FileSizeLimitBytes,
    DateTimeOffset MountedAt)
{
    /// <summary>"X:\" — what to hand to Explorer or a terminal.</summary>
    public string DrivePath => DriveLetter + "\\";

    /// <summary>
    /// The size-limit explanation to show after mounting, or null when the limit is
    /// generous enough not to matter (≥ 4 GB, which is also the redirector's ceiling).
    /// </summary>
    public string? FileSizeLimitNotice
        => FileSizeLimitBytes is { } limit && limit < 4_000_000_000 ? DriveMapper.FileSizeLimitNotice(limit) : null;
}

/// <summary>
/// "Mount in Finder" for Windows: each mounted account gets its own loopback
/// <see cref="WebDavServer"/> (with read cache and sidecar store) mapped to a drive letter
/// through the WebDAV redirector — macOS <c>LoopbackMountService</c>.
///
/// <para>Mounts live only as long as FileFluss does: call <see cref="UnmountAllOnExit"/>
/// from the app's exit path, and <see cref="CleanupOrphans"/> once at launch to remove
/// drives a crashed run left pointing at a dead server.</para>
///
/// <para>UI-agnostic: methods may be called from any thread; <see cref="Changed"/> is raised
/// on the synchronization context the service was created on (the UI thread when created
/// there). Failures throw with a message fit to show (<see cref="DriveMappingException"/>
/// for Windows-side problems, <see cref="InvalidOperationException"/> otherwise).</para>
/// </summary>
public sealed class MountService
{
    private readonly SettingsStore _settings;
    private readonly string _sidecarBase;
    private readonly SynchronizationContext? _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ActiveMount> _mounts = [];
    private readonly Lock _mountsLock = new();

    /// <param name="dataDirectory">The app data folder; sidecar files (desktop.ini, Thumbs.db written through a drive) persist under it.</param>
    public MountService(SettingsStore settings, string dataDirectory)
    {
        _settings = settings;
        _sidecarBase = Path.Combine(dataDirectory, "WebDAV-Mounts");
        _context = SynchronizationContext.Current;
        settings.Changed += (_, name) =>
        {
            if (name is null or nameof(AppSettings.MountReadCacheMaxMB))
            {
                // macOS applied a new budget to the next mount only; live caches can take it now.
                foreach (var mount in Snapshot())
                {
                    mount.Cache.SetMaxBytes(CacheBudgetBytes);
                }
            }
        };
    }

    private sealed record ActiveMount(MountInfo Info, WebDavServer Server, WebDavReadCache Cache);

    /// <summary>A mount was added or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The provider of a mounted account refused a request because its sign-in expired
    /// (Explorer only sees "access denied"); the account id is passed. Raised on the
    /// service's synchronization context.
    /// </summary>
    public event EventHandler<Guid>? NeedsReauthentication;

    public IReadOnlyList<MountInfo> Mounts => Snapshot().Select(m => m.Info).ToList();

    public MountInfo? MountFor(Guid accountId)
    {
        lock (_mountsLock)
        {
            return _mounts.GetValueOrDefault(accountId)?.Info;
        }
    }

    public bool IsMounted(Guid accountId) => MountFor(accountId) is not null;

    /// <summary>WebClient's state, for enabling the menu item or explaining why it's off.</summary>
    public static WebClientState WebClientState => DriveMapper.QueryWebClient();

    /// <summary>The read cache's root for an account (Settings → Storage can show its size).</summary>
    public static string ReadCacheBase => Path.Combine(StagingLocation.Base(), "WebDAV-Cache");

    private long CacheBudgetBytes => Math.Max(0, _settings.Settings.MountReadCacheMaxMB) * 1024L * 1024L;

    /// <summary>
    /// Shows <paramref name="account"/> as a drive in File Explorer. Mounting an account that
    /// is already mounted returns the existing mount (the macOS double-click guard).
    /// </summary>
    public async Task<MountInfo> MountAsync(CloudAccount account, FileProvider provider, CancellationToken cancellationToken = default)
    {
        if (provider.IsLocal)
        {
            throw new InvalidOperationException(L("Only cloud accounts can be mounted as a drive."));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (MountFor(account.Id) is { } existing)
            {
                SupportLogger.Shared.Log($"Reusing the existing mount of {account.DisplayName}", WebDavServer.LogCategory);
                return existing;
            }

            var cache = new WebDavReadCache(Path.Combine(ReadCacheBase, account.Id.ToString("N")), CacheBudgetBytes);
            var sidecar = new WebDavSidecarStore(Path.Combine(_sidecarBase, account.Id.ToString("N")));
            var server = new WebDavServer(provider, new WebDavServerOptions
            {
                MountRoot = account.EffectiveRootPath,
                DisplayName = account.DisplayName,
                ReadCache = cache,
                Sidecar = sidecar,
                UploadReplacesInPlace = ReplacesInPlace(account.ProviderType),
            });
            server.ProviderNeedsReauthentication += (_, _) => Post(() => NeedsReauthentication?.Invoke(this, account.Id));

            string drive;
            try
            {
                await server.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await ProbeAsync(server, cancellationToken).ConfigureAwait(false);
                drive = await DriveMapper.MapAsync(server.UncPath, account.DisplayName, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                cache.Clear();
                throw;
            }

            var info = new MountInfo(
                account.Id,
                account.DisplayName,
                drive,
                account.EffectiveRootPath,
                server.Port,
                server.BaseUri,
                server.UncPath,
                DriveMapper.FileSizeLimitBytes(),
                DateTimeOffset.Now);
            lock (_mountsLock)
            {
                _mounts[account.Id] = new ActiveMount(info, server, cache);
            }

            SupportLogger.Shared.Log($"Mounted {account.DisplayName} as {drive}", WebDavServer.LogCategory);
            RaiseChanged();
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the drive, stops the server and drops the read cache. Unknown ids are ignored.</summary>
    public async Task UnmountAsync(Guid accountId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ActiveMount? mount;
            lock (_mountsLock)
            {
                _mounts.Remove(accountId, out mount);
            }

            if (mount is null)
            {
                return;
            }

            await DriveMapper.UnmapAsync(mount.Info.DriveLetter, mount.Info.UncPath).ConfigureAwait(false);
            await mount.Server.DisposeAsync().ConfigureAwait(false);
            mount.Cache.Clear();
            SupportLogger.Shared.Log($"Unmounted {mount.Info.DisplayName}", WebDavServer.LogCategory);
            RaiseChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnmountAllAsync()
    {
        foreach (var mount in Snapshot())
        {
            await UnmountAsync(mount.Info.AccountId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// For <c>Application.OnExit</c>: unmaps every drive synchronously (bounded, so a hung
    /// redirector can't keep the process alive), so no drive is left pointing at a server
    /// that is about to disappear.
    /// </summary>
    public void UnmountAllOnExit(TimeSpan? timeout = null)
    {
        if (Snapshot().Count == 0)
        {
            return;
        }

        try
        {
            Task.Run(UnmountAllAsync).Wait(timeout ?? TimeSpan.FromSeconds(10));
        }
        catch (AggregateException e)
        {
            SupportLogger.Shared.Log($"Unmounting on exit failed: {e.InnerException?.Message}", WebDavServer.LogCategory, LogLevel.Error);
        }
    }

    /// <summary>
    /// Call once at launch: removes drives (and Explorer labels) a previous run that crashed
    /// or was killed left behind. Returns how many drives were removed.
    /// </summary>
    public static Task<int> CleanupOrphansAsync() => Task.Run(DriveMapper.CleanupOrphans);

    /// <summary>Opens the mounted drive (or a folder on it, given as a provider path) in Explorer.</summary>
    public void OpenInExplorer(Guid accountId, string? providerPath = null)
    {
        if (MountFor(accountId) is not { } mount)
        {
            return;
        }

        var target = mount.DrivePath;
        if (providerPath is not null && CloudPath.IsWithin(providerPath, mount.RootPath))
        {
            var relative = CloudPath.Normalize(providerPath)[CloudPath.Normalize(mount.RootPath).TrimEnd('/').Length..];
            target = Path.Combine(mount.DrivePath, relative.TrimStart('/').Replace('/', '\\'));
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { target }, UseShellExecute = false });
    }

    /// <summary>
    /// Providers whose upload onto an existing path replaces the file in place: a plain PUT
    /// (WebDAV family, S3 family), overwrite-on-write (SFTP, FTP), Google Drive's PATCH of the
    /// cached id. The rest overwrite through a temporary sibling — see
    /// <see cref="WebDavServerOptions.UploadReplacesInPlace"/>.
    /// </summary>
    internal static bool ReplacesInPlace(CloudProviderType type) => type is
        CloudProviderType.WebDav or CloudProviderType.NextCloud or CloudProviderType.GmxCloud
        or CloudProviderType.S3 or CloudProviderType.S3Compatible or CloudProviderType.SynologyC2
        or CloudProviderType.Sftp or CloudProviderType.Ftp or CloudProviderType.GoogleDrive;

    /// <summary>
    /// The macOS self-probe: confirm our own server answers before handing its URL to the
    /// redirector, whose errors are much vaguer.
    /// </summary>
    private static async Task ProbeAsync(WebDavServer server, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, server.BaseUri);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(L("The internal WebDAV server didn't respond with 200 OK to OPTIONS."));
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(L("Could not reach the internal WebDAV server on 127.0.0.1:{0}: {1}", server.Port, e.Message), e);
        }
    }

    private List<ActiveMount> Snapshot()
    {
        lock (_mountsLock)
        {
            return [.. _mounts.Values];
        }
    }

    private void RaiseChanged() => Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
