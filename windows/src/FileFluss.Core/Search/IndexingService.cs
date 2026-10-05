// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Services;

namespace FileFluss.Core.Search;

public enum IndexingJobKind
{
    Drive,
    Cloud,
}

/// <summary>
/// One indexing run, for the sidebar's spinner and count and its "Cancel Indexing" item.
/// Properties change on the UI thread (the one <see cref="IndexingService"/> was created on).
/// </summary>
public sealed class IndexingJob : INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _filesProcessed;
    private string _currentPath = string.Empty;
    private bool _isComplete;
    private bool _isCancelled;
    private string? _errorMessage;
    private int? _storedCount;

    internal IndexingJob(string sourceId, string displayName, IndexingJobKind kind, Guid? accountId)
    {
        SourceId = sourceId;
        DisplayName = displayName;
        Kind = kind;
        AccountId = accountId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The account's id string or the drive's "vol:…" id.</summary>
    public string SourceId { get; }

    public string DisplayName { get; }

    public IndexingJobKind Kind { get; }

    public Guid? AccountId { get; }

    /// <summary>Entries walked so far (reported every few hundred).</summary>
    public int FilesProcessed { get => _filesProcessed; private set => Set(ref _filesProcessed, value); }

    /// <summary>The folder being walked, relative to the source's root.</summary>
    public string CurrentPath { get => _currentPath; private set => Set(ref _currentPath, value); }

    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            if (Set(ref _isComplete, value))
            {
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }

    public bool IsRunning => !IsComplete;

    public bool IsCancelled { get => _isCancelled; private set => Set(ref _isCancelled, value); }

    /// <summary>"Cancelled", "Could not save the index: …", "Account not connected"; null on success.</summary>
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }

    /// <summary>What the index holds after the run — not what was walked; they differ when rows are rejected.</summary>
    public int? StoredCount { get => _storedCount; private set => Set(ref _storedCount, value); }

    /// <summary>Completes once the final state has been applied (and <see cref="IndexingService.IndexingFinished"/> raised).</summary>
    public Task Completion => _completion.Task;

    internal CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>The walk itself, which a following run for the same source waits out.</summary>
    internal Task Walk { get; set; } = Task.CompletedTask;

    /// <summary>Asks the walk to stop. The previous index is kept; nothing half-walked replaces it.</summary>
    public void Cancel() => _cancellation.Cancel();

    internal void Report(int processed, string path)
    {
        FilesProcessed = processed;
        CurrentPath = path;
    }

    /// <summary>Applies the final state; false when the job had already finished.</summary>
    internal bool Finish(int processed, bool cancelled, string? error, int? stored)
    {
        if (IsComplete)
        {
            return false;
        }

        FilesProcessed = processed;
        StoredCount = stored;
        IsCancelled = cancelled;
        ErrorMessage = cancelled ? Localization.L("Cancelled") : error;
        IsComplete = true;
        return true;
    }

    internal void SignalCompletion() => _completion.TrySetResult();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>macOS <c>.indexingDidFinish</c>: refresh the sidebar's "Indexed …" line and Index Status.</summary>
public sealed class IndexingFinishedEventArgs(IndexingJob job) : EventArgs
{
    public IndexingJob Job { get; } = job;

    public string SourceId => Job.SourceId;

    public IndexingJobKind Kind => Job.Kind;

    public Guid? AccountId => Job.AccountId;

    public bool Succeeded => !Job.IsCancelled && Job.ErrorMessage is null;
}

/// <summary>
/// Indexes a drive's file system or a cloud account's tree into the <see cref="SearchIndex"/>,
/// so the files stay searchable — and browsable — while the source is offline. Port of the
/// macOS <c>IndexingService</c>.
///
/// <para>Each run is an <see cref="IndexingJob"/> in <see cref="Jobs"/>. One run per source
/// at a time: starting another cancels the running one and waits for it to stop before
/// walking, so two walks never write the same staging rows. Walks run on the thread pool;
/// the index is written as it is walked, not all at once at the end. Create the service on
/// the UI thread: job state and <see cref="IndexingFinished"/> are delivered there.</para>
/// </summary>
public sealed class IndexingService
{
    /// <summary>
    /// Rows per drive write. Large enough that the per-transaction cost is noise against a
    /// million-file drive, small enough that memory stays flat however big the source is.
    /// </summary>
    public const int WriteBatchSize = 10_000;

    private const int LocalReportInterval = 250;
    private const int CloudReportInterval = 100;
    private const string LogCategory = "Indexing";

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,

        // macOS skips hidden files; here that is the Hidden and System attributes, which also
        // keeps "System Volume Information" and "$RECYCLE.BIN" out of the index.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly SearchIndex _index;
    private readonly ProviderHub _providers;
    private readonly DriveRegistry? _drives;
    private readonly UiContext _ui = UiContext.Capture();
    private readonly Lock _gate = new();

    /// <summary>Each source's latest walk, which the next run for that source waits out.</summary>
    private readonly Dictionary<string, Task> _lastWalk = new(StringComparer.Ordinal);

    public IndexingService(SearchIndex index, ProviderHub providers, DriveRegistry? drives = null)
    {
        _index = index;
        _providers = providers;
        _drives = drives;
    }

    /// <summary>Raised on the UI thread when a run ends — completed, failed or cancelled.</summary>
    public event EventHandler<IndexingFinishedEventArgs>? IndexingFinished;

    /// <summary>Running and finished runs; finished ones stay until <see cref="Remove"/>, so the UI can show how they ended.</summary>
    public ObservableCollection<IndexingJob> Jobs { get; } = [];

    public IndexingJob? ActiveJob(string sourceId)
    {
        lock (_gate)
        {
            return Jobs.FirstOrDefault(j => j.SourceId == sourceId && !j.IsComplete);
        }
    }

    /// <summary>Cancels any running run for the source. The job stays listed, showing it was cancelled.</summary>
    public void Cancel(string sourceId)
    {
        lock (_gate)
        {
            foreach (var job in Jobs.Where(j => j.SourceId == sourceId && !j.IsComplete))
            {
                job.Cancel();
            }
        }
    }

    public void Remove(Guid jobId)
    {
        lock (_gate)
        {
            if (Jobs.FirstOrDefault(j => j.Id == jobId) is { } job)
            {
                job.Cancel();
                Jobs.Remove(job);
            }
        }
    }

    // MARK: - Starting

    /// <summary>Indexes a mounted drive; null when the drive isn't mounted (nothing to walk).</summary>
    public IndexingJob? IndexDrive(string driveId)
        => _drives?.Find(driveId) is { RootPath: { } root } drive ? IndexDrive(drive, root) : null;

    /// <summary>
    /// Starts (or restarts) indexing a drive mounted at <paramref name="rootPath"/>. Paths are
    /// stored relative to the root, so the index survives the drive returning under another letter.
    /// </summary>
    public IndexingJob IndexDrive(KnownDrive drive, string rootPath)
        => Start(drive.Id, drive.DisplayName, IndexingJobKind.Drive, null, job =>
        {
            WalkLocal(job, rootPath, drive.Kind);
            return Task.CompletedTask;
        });

    /// <summary>Indexes a connected cloud account's whole tree into the cloud cache, so it stays searchable while disconnected.</summary>
    public IndexingJob IndexCloudAccount(CloudAccount account)
    {
        var provider = _providers.Get(account.Id);
        var root = account.EffectiveRootPath;
        return Start(SearchIndex.SourceIdFor(account.Id), account.DisplayName, IndexingJobKind.Cloud, account.Id, job => WalkCloudAsync(job, provider, account.Id, root));
    }

    private IndexingJob Start(string sourceId, string displayName, IndexingJobKind kind, Guid? accountId, Func<IndexingJob, Task> walk)
    {
        lock (_gate)
        {
            foreach (var running in Jobs.Where(j => j.SourceId == sourceId && !j.IsComplete))
            {
                running.Cancel();
            }

            // The source's previous walk, even if its job was removed from the list since: the
            // new walk starts only once that one has stopped writing.
            var previous = _lastWalk.GetValueOrDefault(sourceId) ?? Task.CompletedTask;
            var job = new IndexingJob(sourceId, displayName, kind, accountId);
            job.Walk = Task.Run(() => RunWalk(job, previous, () => walk(job)));
            _lastWalk[sourceId] = job.Walk;
            Jobs.Add(job);
            return job;
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or SearchIndexException)
        {
            // The previous run ended badly; it has stopped, which is all that matters here.
        }
    }

    // MARK: - Drive walk

    private void WalkLocal(IndexingJob job, string rootPath, DriveKind kind)
    {
        var sourceId = job.SourceId;
        var token = job.CancellationToken;

        // Rows go to the index in batches as the walk finds them, rather than being held
        // until the end: a 179,000-file share was keeping every entry in memory for eight
        // minutes before any of it was written. Staged rows stay invisible until the whole
        // walk is in, so an interrupted index never replaces a good one.
        var batch = new List<IndexedFile>(WriteBatchSize);
        var requested = 0;
        var rejected = 0;
        var processed = 0;
        var lastReport = 0;
        string? failure = null;

        _index.DiscardStagedFiles(sourceId);

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            // macOS walked nothing and promoted the empty result, replacing a good index with
            // nothing. A drive that isn't there is a failure, not an empty drive.
            Finish(job, 0, cancelled: false, CloudProviderException.NotFound(root).Message, null);
            return;
        }

        var rootLength = Path.TrimEndingDirectorySeparator(root).Length;
        var walk = new FileSystemEnumerable<IndexedFile>(root, (ref FileSystemEntry entry) =>
        {
            var full = entry.ToFullPath();
            var relative = "/" + full[rootLength..].TrimStart('\\', '/').Replace('\\', '/');
            var slash = relative.LastIndexOf('/');
            return new IndexedFile(
                sourceId,
                relative,
                slash <= 0 ? "/" : relative[..slash],
                entry.FileName.ToString(),
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length,
                entry.LastWriteTimeUtc);
        }, WalkOptions)
        {
            // A junction or symlink to a parent folder would otherwise be walked for ever. The
            // link itself is still indexed — and so are cloud placeholders, which are reparse
            // points too.
            ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        try
        {
            foreach (var file in walk)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                batch.Add(file);
                processed++;

                if (batch.Count >= WriteBatchSize)
                {
                    rejected += _index.StageFiles(batch, sourceId);
                    requested += batch.Count;
                    batch.Clear();
                }

                if (processed - lastReport >= LocalReportInterval)
                {
                    lastReport = processed;
                    var snapshot = processed;
                    var path = file.ParentPath;
                    _ui.Post(() => job.Report(snapshot, path));
                }
            }
        }
        catch (SearchIndexException e)
        {
            failure = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The drive went away mid-walk, or a folder turned unreadable in a way the
            // enumerator doesn't skip.
            failure = e.Message;
        }

        var cancelled = token.IsCancellationRequested;
        int? stored = null;

        // Whatever the walk ended with, still unwritten.
        if (!cancelled && failure is null && batch.Count > 0)
        {
            try
            {
                rejected += _index.StageFiles(batch, sourceId);
                requested += batch.Count;
            }
            catch (SearchIndexException e)
            {
                failure = e.Message;
            }
        }

        if (cancelled || failure is not null)
        {
            // Nothing staged becomes the index: a cancelled or failed run leaves the previous
            // one exactly as it was.
            _index.DiscardStagedFiles(sourceId);
        }
        else
        {
            try
            {
                var result = _index.PromoteStagedFiles(sourceId, kind.IndexKind(), job.DisplayName, requested, rejected);
                stored = result.Stored;
                SupportLogger.Shared.Log(
                    $"Indexed {job.DisplayName}: {result.Stored} of {result.Requested} entries stored" + (result.Rejected > 0 ? $", {result.Rejected} rejected" : string.Empty),
                    LogCategory);
                if (!result.IsComplete)
                {
                    SupportLogger.Shared.Log($"Index for {job.DisplayName} is short of the walk: walked {result.Requested}, stored {result.Stored}", LogCategory, LogLevel.Error);
                }
            }
            catch (SearchIndexException e)
            {
                failure = e.Message;
                _index.DiscardStagedFiles(sourceId);
            }
        }

        if (failure is not null)
        {
            SupportLogger.Shared.Log($"Indexing {job.DisplayName} failed to write: {failure}", LogCategory, LogLevel.Error);
        }

        var bytes = stored is not null ? _index.GetSource(sourceId)?.TotalBytes ?? 0 : 0;
        Finish(job, processed, cancelled, failure is null ? null : Localization.L("Could not save the index: {0}", failure), stored, () =>
        {
            // Nothing was stored, so nothing is claimed: the drive keeps whatever it said
            // before. "Indexed N files" after a failed write is what sent a user looking for a
            // database that had never been written. The count is what the index holds.
            if (stored is { } count && _drives is not null)
            {
                _drives.RecordIndexed(sourceId, DateTimeOffset.Now, count, bytes);
            }
        });
    }

    // MARK: - Cloud walk

    private async Task WalkCloudAsync(IndexingJob job, FileProvider? provider, Guid accountId, string rootPath)
    {
        if (provider is null)
        {
            Finish(job, 0, cancelled: false, Localization.L("Account not connected"), null);
            return;
        }

        var token = job.CancellationToken;
        var pending = new Queue<string>();
        pending.Enqueue(rootPath);
        var processed = 0;
        var lastReport = 0;
        var isRoot = true;

        while (pending.Count > 0 && !token.IsCancellationRequested)
        {
            var path = pending.Dequeue();
            IReadOnlyList<CloudFileItem> items;
            try
            {
                items = await provider.ListDirectoryAsync(path, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                if (isRoot)
                {
                    // Nothing was listed at all — recording the account as indexed now would
                    // claim an index that doesn't exist.
                    SupportLogger.Shared.Log($"Indexing {job.DisplayName} could not list the root: {e.Message}", LogCategory, LogLevel.Error);
                    Finish(job, processed, cancelled: false, e.Message, null);
                    return;
                }

                // One unreadable folder doesn't stop the walk; its subtree is simply not indexed.
                SupportLogger.Shared.Log($"Indexing {job.DisplayName} skipped {path}: {e.Message}", LogCategory, LogLevel.Notice);
                continue;
            }

            isRoot = false;

            // Written as it is walked. A completed listing is authoritative for its level, so
            // re-indexing also retires what has been deleted since the last run.
            try
            {
                _index.ApplyListing(accountId, path, items);
            }
            catch (SearchIndexException e)
            {
                SupportLogger.Shared.Log($"Indexing failed to write {items.Count} entries: {e.Detail}", LogCategory, LogLevel.Error);
                Finish(job, processed, cancelled: false, Localization.L("Could not save the index: {0}", e.Message), null);
                return;
            }

            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    pending.Enqueue(item.Path);
                }

                processed++;
            }

            if (processed - lastReport >= CloudReportInterval)
            {
                lastReport = processed;
                var snapshot = processed;
                var current = path;
                _ui.Post(() => job.Report(snapshot, current));
            }
        }

        var cancelled = token.IsCancellationRequested;
        int? stored = null;
        if (!cancelled && _index.CloudAccountSummary(accountId) is { } summary)
        {
            // Mirrored into indexed_sources, so Index Status and the sidebar have one source of
            // truth for "what's been indexed, and when".
            _index.RecordCloudSource(accountId, job.DisplayName, summary);
            stored = summary.TotalFiles + summary.TotalFolders;
        }

        Finish(job, processed, cancelled, null, stored);
    }

    private void Finish(IndexingJob job, int processed, bool cancelled, string? error, int? stored, Action? beforeEvent = null)
        => _ui.Post(() =>
        {
            if (job.IsComplete)
            {
                return;
            }

            try
            {
                beforeEvent?.Invoke();
                job.Finish(processed, cancelled, error, stored);
                IndexingFinished?.Invoke(this, new IndexingFinishedEventArgs(job));
            }
            finally
            {
                job.SignalCompletion();
            }
        });

    /// <summary>Runs a walk; a failure nobody anticipated still ends the job instead of leaving it spinning.</summary>
    private async Task RunWalk(IndexingJob job, Task previous, Func<Task> walk)
    {
        await AwaitQuietly(previous).ConfigureAwait(false);
        try
        {
            await walk().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Indexing {job.DisplayName} failed: {e.Message}", LogCategory, LogLevel.Error);
            if (job.Kind == IndexingJobKind.Drive)
            {
                _index.DiscardStagedFiles(job.SourceId);
            }

            Finish(job, job.FilesProcessed, cancelled: false, e.Message, null);
        }
    }
}
