// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Sync;

/// <summary>
/// Carries out a <see cref="SyncPlan"/>, reporting on a <see cref="TransferProgress"/> that
/// the destination panel's transfer list shows like any copy.
///
/// <para>Files travel through <see cref="TransferEngine"/> — the one place bytes move — one
/// request per destination folder, so same-account syncs copy server-side, cloud ↔ cloud
/// stages through a cleaned-up temp file, upload size limits are enforced and dates are
/// preserved exactly as for a paste. Folders are created and extra items deleted here
/// directly. One result is recorded per plan operation.</para>
/// </summary>
public static class SyncExecutor
{
    /// <summary>How often the byte counters of a running folder batch are copied to the sync's progress.</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The progress for <paramref name="plan"/>: totals known up front, so the bar is
    /// byte-weighted from the first file. Shown under <paramref name="destinationSide"/>.
    /// </summary>
    public static TransferProgress CreateProgress(SyncPlan plan, SyncEndpoint source, SyncEndpoint destination, PanelSide destinationSide)
    {
        var progress = new TransferProgress(plan.Mode.ToTransferOperation(), plan.Operations.Count, destinationSide)
        {
            IsCloudToCloud = source.IsCloud && destination.IsCloud && !ReferenceEquals(source.Provider, destination.Provider),
        };

        var name = source.FolderName;
        progress.SetItems([new CloudFileItem(source.Path, name, source.Path, isDirectory: true, 0, CloudFileItem.UnknownDate)]);
        var directional = plan.DownloadBytes + plan.UploadBytes;
        progress.SetPlan(plan.FilesToAdd + plan.FilesToReplace, directional > 0 ? directional : plan.TotalBytes);
        return progress;
    }

    /// <summary>Runs the plan to completion. Never throws; outcomes are recorded on <paramref name="progress"/>.</summary>
    public static async Task ExecuteAsync(SyncPlan plan, SyncEndpoint source, SyncEndpoint destination, TransferProgress progress)
    {
        var ct = progress.CancellationToken;
        SupportLogger.Shared.Log(
            $"Sync start: {plan.Mode} {plan.Operations.Count} operation(s) → {destination.DisplayPath}",
            "Sync");
        try
        {
            if (source.IsOffline || destination.IsOffline || !source.IsUsable || !destination.IsUsable)
            {
                throw new InvalidOperationException(L("Sync needs a live folder on both sides."));
            }

            progress.SetPhase(progress.IsCloudToCloud ? TransferPhase.Downloading : TransferPhase.Copying);
            await CreateFoldersAsync(plan, destination, progress, ct).ConfigureAwait(false);
            await CopyFilesAsync(plan, source, destination, progress, ct).ConfigureAwait(false);
            await DeleteExtrasAsync(plan, destination, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Recorded by Cancel(); the completion summary says so.
        }
        catch (Exception e)
        {
            progress.Fail(e.Message);
            SupportLogger.Shared.Log($"Sync failed: {e.Message}", "Sync", LogLevel.Error);
        }
        finally
        {
            progress.Complete();
            SupportLogger.Shared.Log($"Sync end: {progress.CompletionSummary}", "Sync");
        }
    }

    // MARK: - Folders

    private static async Task CreateFoldersAsync(SyncPlan plan, SyncEndpoint destination, TransferProgress progress, CancellationToken ct)
    {
        // Plan order is parents first.
        foreach (var op in plan.Operations.Where(o => o.Kind == SyncOperationKind.Add && o.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            progress.BeginFile(op.DisplayName, 0);
            try
            {
                // The planner's destination spelling: legal on the local disk, and inside folders
                // that already exist under their own names.
                await destination.Provider.CreateDirectoryAsync(destination.Resolve(op.DestinationRelativePath), ct).ConfigureAwait(false);
                progress.RecordSuccess(op.DisplayName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                progress.RecordFailure(op.DisplayName, e.Message);
            }
        }
    }

    // MARK: - Files

    private static async Task CopyFilesAsync(SyncPlan plan, SyncEndpoint source, SyncEndpoint destination, TransferProgress progress, CancellationToken ct)
    {
        // One engine request per destination folder: the engine lists the destination once
        // per request, and a request per file would double the API calls of a big sync.
        var groups = new List<List<SyncOperation>>();
        var open = new Dictionary<string, (List<SyncOperation> Ops, HashSet<string> Names)>(StringComparer.Ordinal);
        foreach (var op in plan.Operations.Where(o => o.CopiesFile))
        {
            var folder = ParentOf(op.DestinationRelativePath);
            var name = NameOf(op.RelativePath);
            if (open.TryGetValue(folder, out var group) && group.Names.Add(name))
            {
                group.Ops.Add(op);
                continue;
            }

            // A new folder, or a name differing from one already in the batch only by case
            // (the engine keys a request by name): start another batch.
            group = ([op], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name });
            open[folder] = group;
            groups.Add(group.Ops);
        }

        // Which byte counters a file of this sync moves: the engine reports local copies and
        // downloads as "download", uploads as "upload", cloud ↔ cloud as both. A same-account
        // server-side copy reports nothing and is credited in full afterwards.
        var weighsDownload = source.IsCloud || !destination.IsCloud;
        var weighsUpload = destination.IsCloud;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var folder = ParentOf(group[0].DestinationRelativePath);
            var byName = new Dictionary<string, SyncOperation>(StringComparer.OrdinalIgnoreCase);
            var items = new List<CloudFileItem>(group.Count);
            Dictionary<string, string>? renames = null;
            foreach (var op in group)
            {
                var name = NameOf(op.RelativePath);
                byName[name] = op;
                var path = source.Resolve(op.RelativePath);
                items.Add(new CloudFileItem(path, name, path, isDirectory: false, op.Bytes, op.SourceModificationDate));
                if (op.Kind == SyncOperationKind.AddRenamed)
                {
                    (renames ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = NameOf(op.DestinationRelativePath);
                }
            }

            var request = new TransferRequest(source.Provider, items, destination.Provider, destination.Resolve(folder), IsMove: false)
            {
                // The plan already decided: anything still in the way is replaced.
                ResolveConflict = null,
                DestinationNames = renames,
            };

            var batch = TransferEngine.CreateProgress(request, progress.Panel);
            var bridge = new BatchBridge(progress, batch, byName);
            using (ct.Register(batch.Cancel))
            {
                var run = TransferEngine.RunAsync(request, batch);
                while (!run.IsCompleted)
                {
                    await Task.WhenAny(run, Task.Delay(PumpInterval, CancellationToken.None)).ConfigureAwait(false);
                    bridge.Pump();
                }

                await run.ConfigureAwait(false);
                bridge.Pump();
            }

            // One record per operation, from the engine's per-item results.
            var results = new Dictionary<string, TransferItemResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in batch.Results)
            {
                results.TryAdd(result.Name, result);
            }

            long creditDownload = 0, creditUpload = 0;
            var succeeded = 0;
            foreach (var (name, op) in byName)
            {
                if (results.TryGetValue(name, out var result))
                {
                    switch (result.Status)
                    {
                        case TransferItemStatus.Succeeded:
                            progress.RecordSuccess(op.DisplayName);
                            succeeded++;
                            creditDownload += weighsDownload ? op.Bytes : 0;
                            creditUpload += weighsUpload ? op.Bytes : 0;
                            break;
                        case TransferItemStatus.Failed:
                            progress.RecordFailure(op.DisplayName, result.ErrorMessage ?? L("Unknown error"));
                            break;
                        default:
                            progress.RecordSkip(op.DisplayName);
                            break;
                    }
                }
                else if (batch.ErrorMessage is { } error)
                {
                    progress.RecordFailure(op.DisplayName, error);
                }

                // No result and no error: the batch was cancelled before reaching it.
            }

            progress.TopUp(upload: false, bridge.DownloadForwarded, creditDownload);
            progress.TopUp(upload: true, bridge.UploadForwarded, creditUpload);
            bridge.CreditFiles(succeeded);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Copies a running batch's counters onto the sync's progress. Only the executor's own
    /// flow calls <see cref="Pump"/>, so it needs no lock; the counters it reads are atomic.
    /// </summary>
    private sealed class BatchBridge(TransferProgress overall, TransferProgress batch, IReadOnlyDictionary<string, SyncOperation> byName)
    {
        private int _files;
        private string _currentName = string.Empty;

        public long DownloadForwarded { get; private set; }

        public long UploadForwarded { get; private set; }

        public void Pump()
        {
            var name = batch.CurrentFileName;
            if (name.Length > 0 && name != _currentName)
            {
                _currentName = name;
                overall.BeginFile(byName.TryGetValue(name, out var op) ? op.DisplayName : name, batch.CurrentFileSize);
            }

            var down = batch.DownloadBytes;
            if (down > DownloadForwarded)
            {
                overall.AddDownloadBytes(down - DownloadForwarded);
                DownloadForwarded = down;
            }

            var up = batch.UploadBytes;
            if (up > UploadForwarded)
            {
                overall.AddUploadBytes(up - UploadForwarded);
                UploadForwarded = up;
            }

            CreditFiles(batch.CompletedFiles);
            if (overall.IsCloudToCloud && batch.Phase is TransferPhase.Downloading or TransferPhase.Uploading)
            {
                overall.SetPhase(batch.Phase);
            }
        }

        /// <summary>Advances the sync's file count to <paramref name="files"/> for this batch (server-side copies don't count themselves).</summary>
        public void CreditFiles(int files)
        {
            while (_files < files)
            {
                overall.FileFinished();
                _files++;
            }
        }
    }

    // MARK: - Mirror deletes

    private static async Task DeleteExtrasAsync(SyncPlan plan, SyncEndpoint destination, TransferProgress progress, CancellationToken ct)
    {
        var deletes = plan.Operations.Where(o => o.Kind == SyncOperationKind.Delete).ToList();
        if (deletes.Count == 0)
        {
            return;
        }

        // Deleting a folder takes everything in it, so only the top-most items are deleted;
        // their descendants share the outcome. One call instead of one per file, and a local
        // folder lands in the Recycle Bin whole, restorable in one piece.
        var folders = deletes.Where(o => o.IsDirectory).Select(o => o.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = new Dictionary<string, List<SyncOperation>>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<SyncOperation>();
        foreach (var op in deletes)
        {
            if (TopDeletedAncestor(op.RelativePath, folders) is { } ancestor)
            {
                if (!covered.TryGetValue(ancestor, out var list))
                {
                    list = [];
                    covered[ancestor] = list;
                }

                list.Add(op);
            }
            else
            {
                roots.Add(op);
            }
        }

        foreach (var op in roots)
        {
            ct.ThrowIfCancellationRequested();
            progress.BeginFile(op.DisplayName, 0);
            string? error = null;
            try
            {
                await destination.Provider.DeleteItemAsync(destination.Resolve(op.RelativePath), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
            {
                // Already gone: the goal is met.
            }
            catch (Exception e)
            {
                error = e.Message;
                SupportLogger.Shared.Log($"Sync delete of {op.RelativePath} failed: {e.Message}", "Sync", LogLevel.Error);
            }

            foreach (var item in covered.GetValueOrDefault(op.RelativePath) ?? [])
            {
                Record(item, error);
            }

            Record(op, error);
        }

        void Record(SyncOperation op, string? error)
        {
            if (error is null)
            {
                progress.RecordSuccess(op.DisplayName);
            }
            else
            {
                progress.RecordFailure(op.DisplayName, error);
            }
        }
    }

    private static string? TopDeletedAncestor(string relativePath, HashSet<string> deletedFolders)
    {
        string? top = null;
        var slash = relativePath.LastIndexOf('/');
        while (slash > 0)
        {
            var ancestor = relativePath[..slash];
            if (deletedFolders.Contains(ancestor))
            {
                top = ancestor;
            }

            slash = ancestor.LastIndexOf('/');
        }

        return top;
    }

    private static string ParentOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static string NameOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }
}
