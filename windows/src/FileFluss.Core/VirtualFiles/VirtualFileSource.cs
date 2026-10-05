// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Search;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.VirtualFiles;

/// <summary>
/// The provider side of cloud files dragged or copied out to another app — the Windows
/// counterpart of the macOS file promise. The shell object (FileFluss.Native) asks it for the
/// tree (names for the descriptor) and, entry by entry, for the bytes.
///
/// <para><b>Nothing is fetched until it is asked for.</b> The tree is listed on first use
/// (the pointer leaving FileFluss, a paste, a Ctrl+C); a file is downloaded only when the
/// receiver opens its contents, which Explorer does after the drop, never while hovering.
/// A couple of the following files are fetched ahead, because Explorer reads strictly one
/// file after another and cloud round trips, not bandwidth, dominate small files.</para>
///
/// <para><b>Sessions.</b> Downloads in one burst of requests — one drop, one paste — form a
/// session with one <see cref="TransferProgress"/> (the app shows it in Transfers, where it
/// can be cancelled). A session ends when the receiver says it is done (EndOperation) or
/// nothing has been asked for a few seconds; its staged files are then deleted. Files a
/// receiver still holds open stay readable (they are opened with delete sharing), and a later
/// paste of the same clipboard simply downloads again.</para>
/// </summary>
public sealed class VirtualFileSource : IDisposable
{
    private const string LogCategory = "dragOut";

    /// <summary>Downloads in flight at once: the one being read plus look-ahead.</summary>
    private const int MaxParallelDownloads = 3;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _maxEntries;
    private Task<VirtualFileTree>? _tree;
    private Session? _session;
    private Timer? _idleTimer;
    private int _operations;
    private bool _endRequested;
    private bool _retired;
    private bool _disposed;

    public VirtualFileSource(FileProvider provider, IReadOnlyList<CloudFileItem> items, PanelSide panel, int maxEntries = VirtualFileTree.MaxEntries)
    {
        Provider = provider;
        Items = items.Where(i => i.Role != CloudItemRole.VirtualContainer).ToList();
        Panel = panel;
        _maxEntries = maxEntries;
    }

    public FileProvider Provider { get; }

    public IReadOnlyList<CloudFileItem> Items { get; }

    public PanelSide Panel { get; }

    /// <summary>How long without a request before a session counts as finished.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A download session began (any thread); the app shows its progress.</summary>
    public event Action<TransferProgress>? SessionStarted;

    /// <summary>A session finished and its staged files were deleted (any thread).</summary>
    public event Action<TransferProgress>? SessionEnded;

    /// <summary>Whether the panel's provider can hand out bytes at all: the local disk goes as real files, an offline index has none.</summary>
    public static bool CanProvideContents(FileProvider provider) => !provider.IsLocal && provider is not OfflineIndexProvider;

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    // MARK: - Tree

    public bool IsTreeStarted
    {
        get
        {
            lock (_gate)
            {
                return _tree is not null;
            }
        }
    }

    /// <summary>The listed tree; the first call starts the (background, cancellable) listing.</summary>
    public Task<VirtualFileTree> TreeAsync()
    {
        lock (_gate)
        {
            if (_tree is null)
            {
                var token = _lifetime.Token;
                _tree = Task.Run(() => VirtualFileTree.BuildAsync(Provider, Items, _maxEntries, token), token);
                _tree.ContinueWith(
                    t => SupportLogger.Shared.Log($"Listing {Items.Count} item(s) for another app failed: {t.Exception?.InnerException?.Message}", LogCategory, LogLevel.Error),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }

            return _tree;
        }
    }

    // MARK: - Receiver's operation (IDataObjectAsyncCapability)

    /// <summary>The receiver started an asynchronous extraction (Explorer, after a drop or paste).</summary>
    public void BeginOperation()
    {
        lock (_gate)
        {
            _operations++;
        }
    }

    /// <summary>The receiver finished; the session ends as soon as nothing is in flight.</summary>
    public void EndOperation()
    {
        lock (_gate)
        {
            _operations = Math.Max(0, _operations - 1);
            _endRequested = true;
        }

        CheckIdle();
    }

    /// <summary>
    /// The owner is done with this source (the drag ended, the clipboard moved on). It disposes
    /// itself once no session or receiver operation is still using it.
    /// </summary>
    public void Retire()
    {
        lock (_gate)
        {
            _retired = true;
            _endRequested = true;
        }

        CheckIdle();
    }

    // MARK: - Contents

    /// <summary>
    /// Entry <paramref name="index"/>'s bytes as a file opened for reading, after downloading
    /// it into the staging area if this session hasn't already.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No such entry, or a folder.</exception>
    public async Task<FileStream> OpenAsync(int index)
    {
        var tree = await TreeAsync().ConfigureAwait(false);
        if (index < 0 || index >= tree.Entries.Count || tree.Entries[index].IsDirectory)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Session session;
        Task<string> download;
        TransferProgress? started = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null)
            {
                _session = new Session(this, tree);
                started = _session.Progress;
                _endRequested = false;
            }

            session = _session;
            session.Waiting++;
            session.Touch();
            download = session.Download(index);
        }

        if (started is not null)
        {
            SupportLogger.Shared.Log($"Handing {tree.FileCount} file(s) to another app", LogCategory);
            SessionStarted?.Invoke(started);
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var path = await download.WaitAsync(session.Token).ConfigureAwait(false);
                try
                {
                    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                }
                catch (Exception e) when (attempt == 0 && e is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Deleted under us (a cache clean-up): fetch it once more.
                    lock (_gate)
                    {
                        download = session.Download(index, restart: true);
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                session.Waiting--;
                session.Touch();
            }

            ScheduleIdleCheck(soon: true);
        }
    }

    /// <summary>The receiver is still reading; keeps the session alive.</summary>
    public void Touch()
    {
        lock (_gate)
        {
            _session?.Touch();
        }
    }

    // MARK: - Session end

    /// <summary>
    /// Checks for the end of the session after the idle timeout — or right away when
    /// <paramref name="soon"/> and the receiver has already said it is done.
    /// </summary>
    private void ScheduleIdleCheck(bool soon)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _idleTimer ??= new Timer(_ => CheckIdle());
            _idleTimer.Change(soon && _endRequested && _operations == 0 ? TimeSpan.Zero : IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckIdle()
    {
        Session? ended = null;
        var dispose = false;
        var reschedule = false;
        lock (_gate)
        {
            if (_session is { } session)
            {
                var finished = _endRequested && _operations == 0;
                if (session.Waiting == 0 && session.Running == 0 && (finished || session.IdleFor >= IdleTimeout))
                {
                    ended = session;
                    _session = null;
                }
                else
                {
                    if (finished && session.Waiting == 0)
                    {
                        // The receiver is done: what is still downloading is look-ahead nobody will read.
                        session.CancelDownloads();
                    }

                    reschedule = true;
                }
            }

            dispose = _session is null && _retired && _operations == 0 && !_disposed;
        }

        if (ended is not null)
        {
            ended.Finish();
            SessionEnded?.Invoke(ended.Progress);
        }

        if (reschedule)
        {
            ScheduleIdleCheck(soon: false);
        }

        if (dispose)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        Session? session;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session = _session;
            _session = null;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        _lifetime.Cancel();
        if (session is not null)
        {
            session.CancelDownloads();
            session.Finish();
            SessionEnded?.Invoke(session.Progress);
        }
    }

    /// <summary>One burst of downloads: its progress, staging folder and per-entry downloads. Guarded by the source's lock.</summary>
    private sealed class Session
    {
        private readonly VirtualFileSource _owner;
        private readonly VirtualFileTree _tree;
        private readonly CancellationTokenSource _cancellation;
        private readonly Dictionary<int, Task<string>> _downloads = [];
        private string? _directory;
        private long _lastActivity = Environment.TickCount64;

        public Session(VirtualFileSource owner, VirtualFileTree tree)
        {
            _owner = owner;
            _tree = tree;
            Progress = new TransferProgress(TransferOperation.Downloading, tree.TopLevel.Count, owner.Panel);
            Progress.SetItems(tree.TopLevel);
            Progress.SetPlan(tree.FileCount, tree.KnownBytes);
            Progress.SetPhase(TransferPhase.Downloading);
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner._lifetime.Token, Progress.CancellationToken);
        }

        public TransferProgress Progress { get; }

        public CancellationToken Token => _cancellation.Token;

        public int Waiting { get; set; }

        public int Running { get; private set; }

        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));

        public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

        /// <summary>The download of <paramref name="index"/> (started if needed) plus look-ahead on the next files.</summary>
        public Task<string> Download(int index, bool restart = false)
        {
            var task = Start(index, restart);
            var ahead = 0;
            for (var next = index + 1; next < _tree.Entries.Count && ahead < MaxParallelDownloads - 1 && Running < MaxParallelDownloads; next++)
            {
                if (_tree.Entries[next].IsDirectory)
                {
                    continue;
                }

                ahead++;
                Start(next, false);
            }

            return task;
        }

        private Task<string> Start(int index, bool restart)
        {
            if (!restart && _downloads.TryGetValue(index, out var existing) && !existing.IsFaulted && !existing.IsCanceled)
            {
                return existing;
            }

            Running++;
            var task = Task.Run(() => RunAsync(index));
            _downloads[index] = task;
            return task;
        }

        private async Task<string> RunAsync(int index)
        {
            var entry = _tree.Entries[index];
            try
            {
                var directory = Directory();
                var requested = Path.Combine(directory, index.ToString(CultureInfo.InvariantCulture));
                long reported = 0;
                Progress.BeginFile(entry.Name, entry.Size ?? 0);
                await _owner.Provider.DownloadFileAsync(
                    entry.RemotePath,
                    requested,
                    delta =>
                    {
                        Interlocked.Add(ref reported, delta);
                        Progress.AddDownloadBytes(delta);
                        Touch();
                    },
                    Token).ConfigureAwait(false);

                var actual = DownloadedFile.Locate(requested);
                var length = new FileInfo(actual).Length;
                Progress.TopUp(false, Interlocked.Read(ref reported), length);
                Progress.FileFinished();
                Progress.RecordSuccess(entry.Name);
                return actual;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                SupportLogger.Shared.Log($"Download of \"{entry.RemotePath}\" for another app failed: {e.Message}", LogCategory, LogLevel.Error);
                Progress.RecordFailure(entry.Name, e.Message);
                throw;
            }
            finally
            {
                lock (_owner._gate)
                {
                    Running--;
                    Touch();
                }

                _owner.ScheduleIdleCheck(soon: true);
            }
        }

        private string Directory()
        {
            lock (_owner._gate)
            {
                return _directory ??= StagingLocation.StagingDirectory("dragout");
            }
        }

        public void CancelDownloads() => _cancellation.Cancel();

        /// <summary>Completes the progress and deletes the staged files (open handles keep their data readable).</summary>
        public void Finish()
        {
            Progress.Complete();
            string? directory;
            lock (_owner._gate)
            {
                directory = _directory;
            }

            if (directory is null)
            {
                return;
            }

            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SupportLogger.Shared.Log($"Couldn't remove staged files: {e.Message}", LogCategory, LogLevel.Notice);
            }
        }
    }
}
