// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Formatting;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Storage;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Transfers;

public enum ConflictChoice
{
    Replace,
    Skip,
    KeepBoth,
    Stop,
}

public sealed record ConflictResolution(ConflictChoice Choice, bool ApplyToAll);

/// <summary>An item that already exists at the destination, for the side-by-side dialog.</summary>
public sealed record ConflictInfo(
    CloudFileItem Source,
    CloudFileItem Destination,
    int RemainingConflicts,
    FileProvider SourceProvider,
    FileProvider DestinationProvider);

public delegate Task<ConflictResolution> ConflictResolver(ConflictInfo conflict, CancellationToken cancellationToken);

/// <summary>What to copy or move, from where to where.</summary>
public sealed record TransferRequest(
    FileProvider Source,
    IReadOnlyList<CloudFileItem> Items,
    FileProvider Destination,
    string DestinationPath,
    bool IsMove)
{
    /// <summary>Asks the user about name clashes. Null replaces silently (sync uses its own plan).</summary>
    public ConflictResolver? ResolveConflict { get; init; }

    /// <summary>Keep the source's "Date modified" on the copy, as Explorer and Finder do.</summary>
    public bool PreserveModificationDates { get; init; } = true;

    /// <summary>Name to give each top-level item at the destination, when it differs (sync's "keep both").</summary>
    public IReadOnlyDictionary<string, string>? DestinationNames { get; init; }
}

/// <summary>
/// Copies or moves items between any two <see cref="FileProvider"/>s — local↔local,
/// local↔cloud, cloud↔cloud, same account or not. The single place this happens; paste,
/// drag & drop, the sidebar, F5/F6 and sync all route here, because the rules that make a
/// move safe were the ones that diverged when each call site had its own copy:
///
/// <list type="bullet">
/// <item>A source is deleted only after it arrived — never on a failure, a skip or a cancel.</item>
/// <item>Within one account the server moves or copies; only when it can't do the bytes travel.</item>
/// <item>Cloud↔cloud goes through a staging file that is always cleaned up.</item>
/// <item>Names from the network are made safe before they touch the local disk.</item>
/// </list>
/// </summary>
public static class TransferEngine
{
    /// <summary>What the transfer list calls it, from the two sides and the verb.</summary>
    public static TransferOperation OperationFor(FileProvider source, FileProvider destination, bool isMove)
    {
        if (isMove)
        {
            return TransferOperation.Moving;
        }

        if (source.IsLocal && !destination.IsLocal)
        {
            return TransferOperation.Uploading;
        }

        if (!source.IsLocal && destination.IsLocal)
        {
            return TransferOperation.Downloading;
        }

        return TransferOperation.Copying;
    }

    public static TransferProgress CreateProgress(TransferRequest request, PanelSide panel)
    {
        var progress = new TransferProgress(OperationFor(request.Source, request.Destination, request.IsMove), request.Items.Count, panel)
        {
            IsCloudToCloud = !request.Source.IsLocal && !request.Destination.IsLocal && !ReferenceEquals(request.Source, request.Destination),
        };
        progress.SetItems(request.Items);
        return progress;
    }

    /// <summary>Runs the transfer to completion. Never throws; outcomes are recorded on <paramref name="progress"/>.</summary>
    public static async Task RunAsync(TransferRequest request, TransferProgress progress)
    {
        var ct = progress.CancellationToken;
        SupportLogger.Shared.Log(
            $"Transfer start: {(request.IsMove ? "move" : "copy")} {request.Items.Count} item(s) → {request.DestinationPath}",
            "Transfer");
        try
        {
            await RunCoreAsync(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Recorded by Cancel(); the completion summary says so.
        }
        catch (Exception e)
        {
            // Includes a cancellation nobody asked for — an HTTP timeout surfaces as one, and
            // swallowing it would end the transfer as "Done" with nothing done.
            progress.Fail(Describe(e));
            SupportLogger.Shared.Log($"Transfer failed: {e.Message}", "Transfer", LogLevel.Error);
        }
        finally
        {
            progress.Complete();
            SupportLogger.Shared.Log($"Transfer end: {progress.CompletionSummary}", "Transfer");
        }
    }

    private static async Task RunCoreAsync(TransferRequest request, TransferProgress progress, CancellationToken ct)
    {
        var source = request.Source;
        var destination = request.Destination;
        var sameProvider = ReferenceEquals(source, destination);

        // What is already at the destination, for conflict detection and unique naming. Names
        // match as the destination folds them, and regardless of Unicode form (FileNames).
        var matcher = MatcherFor(destination);
        var existing = await ListOrEmptyAsync(destination, request.DestinationPath, ct).ConfigureAwait(false);
        var existingByName = new Dictionary<string, CloudFileItem>(matcher);
        foreach (var item in existing)
        {
            existingByName.TryAdd(item.Name, item);
        }

        var takenNames = new HashSet<string>(existingByName.Keys, matcher);

        // Names this batch has already claimed. Two sources can land on one name — "a.txt" and
        // "A.txt" from a case-sensitive store onto NTFS, or "a:b" and "a_b" once made legal —
        // and the second would silently overwrite the first (and a move would then delete both).
        var plannedNames = new HashSet<string>(matcher);
        var resolutions = await ResolveConflictsAsync(request, existingByName, ct).ConfigureAwait(false);

        // Plan the whole tree first: real totals for the progress bar, and one listing pass.
        progress.SetPhase(TransferPhase.Preparing);
        var plans = new List<ItemPlan>();
        long expectedBytes = 0;
        var totalFiles = 0;
        foreach (var (item, choice) in resolutions)
        {
            ct.ThrowIfCancellationRequested();
            if (choice is ConflictChoice.Skip or ConflictChoice.Stop)
            {
                progress.RecordSkip(item.Name);
                continue;
            }

            if (!item.IsModifiable && request.IsMove)
            {
                progress.RecordFailure(item.Name, L("\"{0}\" can't be moved.", item.Name));
                continue;
            }

            if (sameProvider && IsSelfOrDescendant(source, item, request.DestinationPath))
            {
                progress.RecordFailure(item.Name, L("A folder can't be copied or moved into itself."));
                continue;
            }

            var targetName = TargetNameFor(request, item);
            if (!destination.IsLocal && CloudPath.IsTraversalName(targetName))
            {
                // A hostile source listing "..": it must not climb out of the destination folder.
                progress.RecordFailure(item.Name, SafeLocalPath.UnsafeName(targetName).Message);
                continue;
            }

            var clash = existingByName.GetValueOrDefault(targetName);

            // Moving /A/foo/foo to /A: the item it would replace is a folder holding the item
            // itself, so "Replace" would delete both. (A folder onto a folder merges instead.)
            if (sameProvider && choice == ConflictChoice.Replace && !item.IsDirectory &&
                clash is { IsDirectory: true } && IsInside(source, item.Path, clash.Path))
            {
                progress.RecordFailure(item.Name, L("“{0}” can't replace a folder it's inside.", item.Name));
                continue;
            }

            // Copying onto itself (same folder, same account) is Explorer's "- Copy".
            var sameFolder = sameProvider && PathsEqual(source, source.GetParentPath(item.Path), request.DestinationPath);
            if (sameFolder)
            {
                if (request.IsMove)
                {
                    progress.RecordSkip(item.Name);
                    continue;
                }

                targetName = UniqueName(targetName, takenNames);
                clash = null;
            }
            else if (choice == ConflictChoice.KeepBoth || plannedNames.Contains(targetName))
            {
                targetName = UniqueName(targetName, takenNames);
                clash = null;
            }
            else if (choice == ConflictChoice.Replace && clash is not null && clash.IsDirectory == item.IsDirectory &&
                     (item.IsDirectory || destination.IsLocal))
            {
                // Land on the existing item's own spelling. NTFS keeps "Café" in NFC and NFD as
                // two files, so writing the other form would put a twin beside the original
                // instead of replacing it; a merged folder must be the folder that is there.
                targetName = clash.Name;
            }

            takenNames.Add(targetName);
            plannedNames.Add(targetName);
            var plan = new ItemPlan(item, targetName, choice == ConflictChoice.Replace && clash is not null, clash);
            plans.Add(plan);

            if (UsesServerSide(request, plan))
            {
                // Server-side: no bytes counted, the tree walk would only cost time.
                continue;
            }

            if (item.IsDirectory)
            {
                plan.Tree = await WalkAsync(source, item, ct).ConfigureAwait(false);
                totalFiles += plan.Tree.Count(e => !e.Item.IsDirectory);
                expectedBytes += plan.Tree.Where(e => !e.Item.IsDirectory).Sum(e => Math.Max(0, e.Item.Size));
            }
            else
            {
                totalFiles++;
                expectedBytes += Math.Max(0, item.Size);
            }
        }

        // Between two accounts every file passes through the staging folder, which may be on a
        // nearly full disk the destination's quota says nothing about. The sizes are summed
        // already, so checking costs nothing; stopping here with a reason beats failing half
        // way with a disk-full error (macOS 1.6). Windows stages one file at a time.
        if (progress.IsCloudToCloud && StagingNeedFor(plans) is { WouldExceed: true } need)
        {
            var message = L(
                "The files pass through this Mac on the way between the two accounts: that needs {0} on {1}, which has {2} free. Free up space, or choose another folder in Settings → Storage.",
                ByteFormatter.Format(need.Needed),
                need.VolumeName,
                ByteFormatter.Format(Math.Max(0, need.Free ?? 0)));
            SupportLogger.Shared.Log($"Cloud-to-cloud refused: needs {need.Needed} bytes staged on {need.VolumeName}, {need.Free} free", "Transfer", LogLevel.Error);
            progress.Fail(message);
            return;
        }

        progress.SetPlan(totalFiles, progress.IsCloudToCloud ? expectedBytes * 2 : expectedBytes);
        progress.SetPhase(progress.IsCloudToCloud ? TransferPhase.Downloading : TransferPhase.Copying);

        string? staging = null;
        try
        {
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                var targetPath = destination.CombinePath(request.DestinationPath, plan.TargetName);
                try
                {
                    if (UsesServerSide(request, plan) && await TryServerSideAsync(request, plan, targetPath, ct).ConfigureAwait(false))
                    {
                        progress.RecordSuccess(plan.Item.Name);
                        continue;
                    }

                    if (plan.Tree is null && plan.Item.IsDirectory)
                    {
                        // Server-side failed over to a byte copy; the tree wasn't walked yet.
                        plan.Tree = await WalkAsync(source, plan.Item, ct).ConfigureAwait(false);
                    }

                    if (progress.IsCloudToCloud || (!source.IsLocal && !destination.IsLocal))
                    {
                        staging ??= StagingLocation.StagingDirectory("transfer");
                    }

                    var outcome = await CopyItemAsync(request, plan, targetPath, staging, progress, ct).ConfigureAwait(false);
                    var failures = outcome.Failures;
                    if (failures.Count > 0)
                    {
                        var message = failures.Count == 1 && plan.Tree is null
                            ? failures[0]
                            : L("{0} of {1} files failed: {2}", failures.Count, Math.Max(1, plan.Tree?.Count(e => !e.Item.IsDirectory) ?? 1), failures[0]);
                        progress.RecordFailure(plan.Item.Name, message);
                        continue;
                    }

                    if (request.IsMove)
                    {
                        // The source goes only once the copy is confirmed whole at the
                        // destination: a provider that reported success for a truncated upload
                        // must not cost the user the only complete copy.
                        if (await VerifyArrivalsAsync(request, outcome.Arrivals, ct).ConfigureAwait(false) is { } incomplete)
                        {
                            progress.RecordFailure(plan.Item.Name, L("\"{0}\" didn't arrive complete, so the original was kept.", incomplete));
                            SupportLogger.Shared.Log($"Move of {plan.Item.Name}: {incomplete} failed verification, source kept", "Transfer", LogLevel.Error);
                            continue;
                        }

                        await DeleteSourceAsync(source, plan, outcome.Arrivals, ct).ConfigureAwait(false);
                    }

                    progress.RecordSuccess(plan.Item.Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    progress.RecordFailure(plan.Item.Name, Describe(e));
                    SupportLogger.Shared.Log($"Transfer of {plan.Item.Name} failed: {e.Message}", "Transfer", LogLevel.Error);
                }
            }
        }
        finally
        {
            if (staging is not null)
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    /// <summary>The largest file of the planned byte copies: what the staging folder must hold at once.</summary>
    private static StagingNeed? StagingNeedFor(IEnumerable<ItemPlan> plans)
    {
        long largest = 0;
        foreach (var plan in plans)
        {
            if (plan.Tree is { } tree)
            {
                foreach (var entry in tree)
                {
                    if (!entry.Item.IsDirectory)
                    {
                        largest = Math.Max(largest, entry.Item.Size);
                    }
                }
            }
            else if (!plan.Item.IsDirectory)
            {
                largest = Math.Max(largest, plan.Item.Size);
            }
        }

        return StagingSpace.For(largest);
    }

    // MARK: - Conflicts

    private static async Task<List<(CloudFileItem Item, ConflictChoice? Choice)>> ResolveConflictsAsync(
        TransferRequest request,
        Dictionary<string, CloudFileItem> existingByName,
        CancellationToken ct)
    {
        var results = new List<(CloudFileItem, ConflictChoice?)>();
        ConflictChoice? applyToAll = null;
        var sameProvider = ReferenceEquals(request.Source, request.Destination);
        var items = request.Items;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var targetName = TargetNameFor(request, item);
            var inSameFolder = sameProvider && PathsEqual(request.Source, request.Source.GetParentPath(item.Path), request.DestinationPath);
            if (inSameFolder || !existingByName.TryGetValue(targetName, out var clash))
            {
                results.Add((item, null));
                continue;
            }

            ConflictChoice choice;
            if (request.ResolveConflict is null)
            {
                choice = ConflictChoice.Replace;
            }
            else if (applyToAll is { } saved)
            {
                choice = saved;
            }
            else
            {
                var remaining = items.Skip(index + 1).Count(i => existingByName.ContainsKey(TargetNameFor(request, i)));
                var resolution = await request.ResolveConflict(
                    new ConflictInfo(item, clash, remaining, request.Source, request.Destination), ct).ConfigureAwait(false);
                choice = resolution.Choice;
                if (resolution.ApplyToAll)
                {
                    applyToAll = choice;
                }
            }

            if (choice == ConflictChoice.Stop)
            {
                for (var rest = index; rest < items.Count; rest++)
                {
                    results.Add((items[rest], ConflictChoice.Stop));
                }

                return results;
            }

            results.Add((item, choice));
        }

        return results;
    }

    /// <summary>The name an item gets at the destination: sync's rename if any, made legal on the local disk.</summary>
    private static string TargetNameFor(TransferRequest request, CloudFileItem item)
    {
        var name = request.DestinationNames?.GetValueOrDefault(item.Name) ?? item.Name;
        return request.Destination.IsLocal ? SafeLocalPath.FileName(name) : name;
    }

    private static StringComparer MatcherFor(FileProvider destination) => FileNames.MatchComparer(!destination.IsCaseSensitive);

    /// <summary>
    /// What the user reads for a failure. A cancellation nobody requested is a timeout, and a
    /// full disk says so in the user's language rather than as a raw Win32 message.
    /// </summary>
    internal static string Describe(Exception e) => e switch
    {
        OperationCanceledException => L("The request timed out."),
        IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) } => L("Not enough space"),
        _ => e.Message,
    };

    /// <summary>"name (2).ext" — Explorer's rule — unique against <paramref name="taken"/>.</summary>
    public static string UniqueName(string name, ISet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var counter = 2; ; counter++)
        {
            var candidate = $"{stem} ({counter}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // MARK: - Server-side

    private static bool NeedsMerge(ItemPlan plan)
        => plan.Replace && plan.Existing is { IsDirectory: true } && plan.Item.IsDirectory;

    /// <summary>
    /// Whether the provider should move/copy this item itself. Within one cloud account,
    /// always (the bytes never leave the server). On the local disk only a move within one
    /// volume — a rename; copies stream with progress, as Explorer's do.
    /// </summary>
    private static bool UsesServerSide(TransferRequest request, ItemPlan plan)
    {
        if (!ReferenceEquals(request.Source, request.Destination) || NeedsMerge(plan))
        {
            return false;
        }

        if (!request.Source.IsLocal)
        {
            return true;
        }

        return request.IsMove && string.Equals(
            Path.GetPathRoot(Path.GetFullPath(plan.Item.Path)),
            Path.GetPathRoot(Path.GetFullPath(request.DestinationPath)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryServerSideAsync(TransferRequest request, ItemPlan plan, string targetPath, CancellationToken ct)
    {
        var provider = request.Source;

        // Replace sets the old item aside rather than deleting it, so a move that fails, or a
        // provider that turns out not to support moves at all, can put it back (macOS 1.6).
        string? setAside = null;
        if (plan.Replace && plan.Existing is { } existing)
        {
            var asideName = TemporaryReplacementName(existing.Name);
            try
            {
                await provider.RenameItemAsync(existing.Path, asideName, ct).ConfigureAwait(false);
                setAside = provider.CombinePath(provider.GetParentPath(existing.Path), asideName);
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotImplemented)
            {
                // Nothing can be set aside here; clear the way as before.
                await DeleteExistingAsync(provider, existing.Path, ct).ConfigureAwait(false);
            }
        }

        try
        {
            if (request.IsMove)
            {
                await provider.MoveItemAsync(plan.Item.Path, targetPath, ct).ConfigureAwait(false);
            }
            else
            {
                await provider.CopyItemAsync(plan.Item.Path, targetPath, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            if (setAside is not null && plan.Existing is { } original)
            {
                await TryRestoreAsync(provider, setAside, original.Name).ConfigureAwait(false);
            }

            if (e is CloudProviderException { Kind: CloudErrorKind.NotImplemented })
            {
                return false;
            }

            throw;
        }

        if (setAside is not null)
        {
            try
            {
                await DeleteExistingAsync(provider, setAside, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SupportLogger.Shared.Log($"Replaced item kept as {setAside}: {e.Message}", "Transfer", LogLevel.Error);
            }
        }

        return true;
    }

    private static async Task TryRestoreAsync(FileProvider provider, string setAside, string name)
    {
        try
        {
            await provider.RenameItemAsync(setAside, name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Couldn't put {setAside} back as {name}: {e.Message}", "Transfer", LogLevel.Error);
        }
    }

    // MARK: - Replacing

    /// <summary>
    /// A name for the new copy while it's being written next to the item it will replace
    /// (macOS <c>FileSystemService.temporaryReplacementName</c>). Visible on purpose: if the app
    /// dies part way, whatever is left can be seen and deleted. The extension stays last
    /// because some servers judge an upload by it.
    /// </summary>
    public static string TemporaryReplacementName(string name)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[(dot + 1)..]) : (name, string.Empty);

        // Leaves room in the 255 characters most file systems allow for a name.
        var root = stem.Length > 150 ? stem[..150] : stem;
        var temporary = $"{root}.filefluss-part-{tag}";
        return extension.Length == 0 ? temporary : $"{temporary}.{extension}";
    }

    /// <summary>
    /// Replaces the item at <paramref name="existingPath"/> with one <paramref name="write"/>
    /// creates, without ever leaving neither (macOS <c>CloudProvider.replacingItem</c>).
    /// <paramref name="write"/> gets a temporary path beside the old item and returns where the
    /// new one landed (an exported document gains an extension). Only once it has finished does
    /// the old item go and the new one take its place, so a failed upload, a full account or a
    /// cancel leaves things as they were.
    /// </summary>
    /// <returns>The final path of the new item.</returns>
    private static async Task<string> ReplaceAsync(
        FileProvider provider, string existingPath, string finalName, Func<string, Task<string>> write, CancellationToken ct)
    {
        var parent = provider.GetParentPath(existingPath);
        var temporaryName = TemporaryReplacementName(finalName);
        var temporaryPath = provider.CombinePath(parent, temporaryName);
        string landed = temporaryPath;
        try
        {
            landed = await write(temporaryPath).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await DeleteExistingAsync(provider, existingPath, ct).ConfigureAwait(false);
        }
        catch
        {
            // Whichever step failed, the old item is still there.
            await TryDeleteQuietlyAsync(provider, landed).ConfigureAwait(false);
            if (landed != temporaryPath)
            {
                await TryDeleteQuietlyAsync(provider, temporaryPath).ConfigureAwait(false);
            }

            throw;
        }

        // "Report.filefluss-part-1a2b3c4d.docx" → "Report.docx": whatever the write appended stays.
        var landedName = provider.GetName(landed);
        var name = landedName.StartsWith(temporaryName, StringComparison.Ordinal)
            ? finalName + landedName[temporaryName.Length..]
            : finalName;
        try
        {
            // Past the point of no return: the old item is gone, so a cancel must not stop this.
            await provider.RenameItemAsync(landed, name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Replacement {landed} couldn't be renamed to {name}: {e.Message}", "Transfer", LogLevel.Error);
            throw CloudProviderException.CommandFailed(
                L("The new copy of “{0}” was uploaded, but it couldn't be renamed and is still called “{1}”.", name, landedName), e);
        }

        return provider.CombinePath(parent, name);
    }

    private static async Task TryDeleteQuietlyAsync(FileProvider provider, string path)
    {
        try
        {
            if (provider.IsLocal)
            {
                // A half-written copy isn't worth a place in the Recycle Bin.
                await Task.Run(() => LocalFileProvider.DeletePermanently(path)).ConfigureAwait(false);
            }
            else
            {
                await provider.DeleteItemAsync(path, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Never written, or already gone: nothing to clean up.
        }
    }

    // MARK: - Byte copies

    /// <summary>A file this transfer wrote, for verifying a move before its source goes.</summary>
    private sealed record Arrival(string SourcePath, string DestinationPath, long ExpectedSize);

    private sealed class CopyOutcome
    {
        public List<string> Failures { get; } = [];

        public List<Arrival> Arrivals { get; } = [];
    }

    private static async Task<CopyOutcome> CopyItemAsync(
        TransferRequest request,
        ItemPlan plan,
        string targetPath,
        string? staging,
        TransferProgress progress,
        CancellationToken ct)
    {
        var destination = request.Destination;
        var outcome = new CopyOutcome();

        if (!plan.Item.IsDirectory)
        {
            try
            {
                if (plan.Replace && plan.Existing is { } existing && (existing.IsDirectory || !destination.IsLocal))
                {
                    // A local file is overwritten by the staged copy only once that is complete
                    // (the old one survives a failure). Anywhere else the new copy is written
                    // beside the old item under a temporary name and swapped in when whole:
                    // deleting first lost the old version to any failed upload.
                    if (destination.CanVerifyArrivals)
                    {
                        Arrival? written = null;
                        var final = await ReplaceAsync(destination, existing.Path, plan.TargetName, async temporary =>
                        {
                            written = await CopyFileAsync(request, plan.Item, temporary, staging, progress, ct).ConfigureAwait(false);
                            return written.DestinationPath;
                        }, ct).ConfigureAwait(false);
                        outcome.Arrivals.Add(written! with { DestinationPath = final });
                        await RestampAfterRenameAsync(request, plan.Item, final, ct).ConfigureAwait(false);
                        return outcome;
                    }

                    // An upload that doesn't land where it was sent (WordPress files it under
                    // the month) can't be renamed into place; clear the way as before.
                    await DeleteExistingAsync(destination, existing.Path, ct).ConfigureAwait(false);
                }

                outcome.Arrivals.Add(await CopyFileAsync(request, plan.Item, targetPath, staging, progress, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                outcome.Failures.Add(Describe(e));
            }

            return outcome;
        }

        if (plan.Replace && plan.Existing is { IsDirectory: false } fileInTheWay)
        {
            if (destination.CanVerifyArrivals)
            {
                // A folder replacing a file: built beside it under a temporary name, swapped in
                // only when every file arrived.
                try
                {
                    var built = string.Empty;
                    var final = await ReplaceAsync(destination, fileInTheWay.Path, plan.TargetName, async temporary =>
                    {
                        built = temporary;
                        await CopyTreeAsync(request, plan, temporary, staging, progress, outcome, ct).ConfigureAwait(false);
                        if (outcome.Failures.Count > 0)
                        {
                            throw CloudProviderException.CommandFailed(outcome.Failures[0]);
                        }

                        return temporary;
                    }, ct).ConfigureAwait(false);

                    // What arrived did so under the temporary folder, which now has its real name.
                    var written = outcome.Arrivals.ToList();
                    outcome.Arrivals.Clear();
                    foreach (var arrival in written)
                    {
                        outcome.Arrivals.Add(arrival with { DestinationPath = final + arrival.DestinationPath[built.Length..] });
                    }

                    await RestampAfterRenameAsync(request, plan.Item, final, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    if (outcome.Failures.Count == 0)
                    {
                        outcome.Failures.Add(Describe(e));
                    }
                }

                return outcome;
            }

            await DeleteExistingAsync(destination, fileInTheWay.Path, ct).ConfigureAwait(false);
        }

        await CopyTreeAsync(request, plan, targetPath, staging, progress, outcome, ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Some servers count a rename as a change; the copy keeps the source's date all the same.</summary>
    private static async Task RestampAfterRenameAsync(TransferRequest request, CloudFileItem item, string path, CancellationToken ct)
    {
        if (request.PreserveModificationDates && item.HasKnownModificationDate && !request.Destination.IsLocal)
        {
            await TryStampAsync(request.Destination, path, item.ModificationDate, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies a top-level folder's tree to <paramref name="targetPath"/>, recording what arrived
    /// and what failed on <paramref name="outcome"/>.
    /// </summary>
    private static async Task CopyTreeAsync(
        TransferRequest request,
        ItemPlan plan,
        string targetPath,
        string? staging,
        TransferProgress progress,
        CopyOutcome outcome,
        CancellationToken ct)
    {
        var destination = request.Destination;

        // Folders merge into an existing folder of the same name, as Explorer does.
        await destination.CreateDirectoryAsync(targetPath, ct).ConfigureAwait(false);
        var createdFolders = new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = targetPath };

        // Per destination folder: what was there before (listed lazily, and only for folders
        // that existed — a merge), and what this transfer has put there so far.
        var matcher = MatcherFor(destination);
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        if (!NeedsMerge(plan))
        {
            fresh.Add(targetPath);
        }

        var existingIn = new Dictionary<string, Dictionary<string, CloudFileItem>>(StringComparer.Ordinal);
        var placedIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in plan.Tree ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (!createdFolders.TryGetValue(entry.ParentKey, out var parentPath))
            {
                outcome.Failures.Add(L("Item not found: {0}", entry.ParentKey));
                continue;
            }

            try
            {
                if (!existingIn.TryGetValue(parentPath, out var before))
                {
                    before = new Dictionary<string, CloudFileItem>(matcher);
                    if (!fresh.Contains(parentPath))
                    {
                        foreach (var child in await ListOrEmptyAsync(destination, parentPath, ct).ConfigureAwait(false))
                        {
                            before.TryAdd(child.Name, child);
                        }
                    }

                    existingIn[parentPath] = before;
                }

                if (!placedIn.TryGetValue(parentPath, out var placed))
                {
                    placed = new HashSet<string>(matcher);
                    placedIn[parentPath] = placed;
                }

                var name = destination.IsLocal ? SafeLocalPath.FileName(entry.Item.Name) : entry.Item.Name;
                if (!destination.IsLocal && CloudPath.IsTraversalName(name))
                {
                    // Its children are skipped too: their parent key never gets a created folder.
                    outcome.Failures.Add($"{entry.Item.Name}: {SafeLocalPath.UnsafeName(name).Message}");
                    continue;
                }

                CloudFileItem? clash = null;
                if (placed.Contains(name))
                {
                    // Another entry of this folder already landed on this name (case folding,
                    // Unicode form, or characters Windows can't hold): number this one.
                    var taken = new HashSet<string>(placed, matcher);
                    taken.UnionWith(before.Keys);
                    name = UniqueName(name, taken);
                }
                else if (before.TryGetValue(name, out clash) && clash.IsDirectory == entry.Item.IsDirectory)
                {
                    // Merging: land on the existing item's own spelling (see the top level).
                    name = clash.Name;
                }

                placed.Add(name);
                var path = destination.CombinePath(parentPath, name);
                if (entry.Item.IsDirectory)
                {
                    await destination.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
                    createdFolders[entry.Key] = path;
                    if (clash is null)
                    {
                        fresh.Add(path);
                    }

                    continue;
                }

                if (clash is { IsDirectory: false } && !destination.IsLocal)
                {
                    // Merging into an existing cloud folder: providers that keep duplicates
                    // would end up with two, so the new copy is written beside the old one
                    // and swapped in once complete.
                    if (destination.CanVerifyArrivals)
                    {
                        Arrival? written = null;
                        var final = await ReplaceAsync(destination, clash.Path, name, async temporary =>
                        {
                            written = await CopyFileAsync(request, entry.Item, temporary, staging, progress, ct).ConfigureAwait(false);
                            return written.DestinationPath;
                        }, ct).ConfigureAwait(false);
                        outcome.Arrivals.Add(written! with { DestinationPath = final });
                        await RestampAfterRenameAsync(request, entry.Item, final, ct).ConfigureAwait(false);
                        continue;
                    }

                    await DeleteExistingAsync(destination, clash.Path, ct).ConfigureAwait(false);
                }

                outcome.Arrivals.Add(await CopyFileAsync(request, entry.Item, path, staging, progress, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // A folder that couldn't be created never enters createdFolders, so its
                // children fail with "not found" rather than landing somewhere else.
                outcome.Failures.Add($"{entry.Item.Name}: {Describe(e)}");
            }
        }

        if (request.PreserveModificationDates && plan.Item.HasKnownModificationDate)
        {
            await TryStampAsync(destination, targetPath, plan.Item.ModificationDate, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Clears the item a "Replace" overwrites. Already gone (a retried fallback, another client) is fine.</summary>
    private static async Task DeleteExistingAsync(FileProvider provider, string path, CancellationToken ct)
    {
        try
        {
            await provider.DeleteItemAsync(path, ct).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
        }
    }

    /// <returns>Where the file landed and how big it should be there (-1 when unknowable, as for an exported document).</returns>
    private static async Task<Arrival> CopyFileAsync(
        TransferRequest request,
        CloudFileItem item,
        string targetPath,
        string? staging,
        TransferProgress progress,
        CancellationToken ct)
    {
        var source = request.Source;
        var destination = request.Destination;
        var expectedSize = item.Size;
        progress.BeginFile(item.Name, item.Size);

        if (destination is CloudProvider cloud && source.IsLocal)
        {
            CloudProviderException.EnforceUploadSizeLimit(item.Path, cloud.MaxUploadFileSize);
        }

        long reported = 0;
        if (source.IsLocal && destination.IsLocal)
        {
            await LocalFileProvider.CopyFileHandler(item.Path, targetPath, d => { reported += d; progress.AddDownloadBytes(d); }, ct).ConfigureAwait(false);
            progress.TopUp(upload: false, reported, item.Size);
        }
        else if (source.IsLocal)
        {
            await destination.UploadFileAsync(item.Path, targetPath, d => { Interlocked.Add(ref reported, d); progress.AddUploadBytes(d); }, ct).ConfigureAwait(false);
            progress.TopUp(upload: true, reported, item.Size);
        }
        else if (destination.IsLocal)
        {
            await source.DownloadFileAsync(item.Path, targetPath, d => { Interlocked.Add(ref reported, d); progress.AddDownloadBytes(d); }, ct).ConfigureAwait(false);
            progress.TopUp(upload: false, reported, item.Size);

            // An exported Google document lands as "<name>.docx"; stamp that file.
            var landed = DownloadedFile.Locate(targetPath);
            if (DownloadedFile.AddedExtension(targetPath, landed).Length > 0)
            {
                expectedSize = -1;
            }

            targetPath = landed;

            // Whatever comes down from an account came from someone else's server, as a
            // browser download does: SmartScreen and Protected View check it when opened
            // (macOS 1.6 quarantines every download for Gatekeeper the same way).
            MarkOfTheWeb.Apply(landed);
        }
        else if (ReferenceEquals(source, destination) && await TryServerSideFileCopyAsync(source, item, targetPath, ct).ConfigureAwait(false))
        {
            // A same-account file inside a merge: the server copied it.
        }
        else
        {
            // Cloud → cloud: stage locally, then upload. Every byte travels twice.
            var requested = SafeLocalPath.Destination(Guid.NewGuid().ToString("N") + "-" + item.Name, staging ?? StagingLocation.StagingDirectory("transfer"));
            var staged = requested;
            try
            {
                progress.SetPhase(TransferPhase.Downloading);
                await source.DownloadFileAsync(item.Path, requested, d => { Interlocked.Add(ref reported, d); progress.AddDownloadBytes(d); }, ct).ConfigureAwait(false);
                progress.TopUp(upload: false, reported, item.Size);

                // A Google document arrives exported ("<name>.docx"); it travels on under that name.
                staged = DownloadedFile.Locate(requested);
                var added = DownloadedFile.AddedExtension(requested, staged);
                if (added.Length > 0)
                {
                    expectedSize = -1;
                    if (!targetPath.EndsWith(added, StringComparison.OrdinalIgnoreCase))
                    {
                        targetPath += added;
                    }
                }

                if (item.HasKnownModificationDate)
                {
                    // Some providers (Nextcloud's X-OC-Mtime) carry the local mtime through upload.
                    TrySetLocalDate(staged, item.ModificationDate);
                }

                if (destination is CloudProvider cloudDestination)
                {
                    CloudProviderException.EnforceUploadSizeLimit(staged, cloudDestination.MaxUploadFileSize);
                }

                progress.SetPhase(TransferPhase.Uploading);
                long uploaded = 0;
                await destination.UploadFileAsync(staged, targetPath, d => { Interlocked.Add(ref uploaded, d); progress.AddUploadBytes(d); }, ct).ConfigureAwait(false);
                progress.TopUp(upload: true, uploaded, item.Size);
            }
            finally
            {
                TryDeleteFile(staged);
                TryDeleteFile(requested);
                progress.SetPhase(TransferPhase.Downloading);
            }
        }

        if (request.PreserveModificationDates && item.HasKnownModificationDate)
        {
            await TryStampAsync(destination, targetPath, item.ModificationDate, ct).ConfigureAwait(false);
        }

        progress.FileFinished();
        return new Arrival(item.Path, targetPath, expectedSize);
    }

    private static async Task<bool> TryServerSideFileCopyAsync(FileProvider provider, CloudFileItem item, string targetPath, CancellationToken ct)
    {
        try
        {
            await provider.CopyItemAsync(item.Path, targetPath, ct).ConfigureAwait(false);
            return true;
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotImplemented)
        {
            return false;
        }
    }

    private static async Task TryStampAsync(FileProvider destination, string path, DateTimeOffset date, CancellationToken ct)
    {
        if (destination.IsLocal)
        {
            TrySetLocalDate(path, date);
            return;
        }

        try
        {
            await destination.SetModificationDateAsync(path, date, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort: providers without the API keep the upload time.
        }
    }

    private static void TrySetLocalDate(string path, DateTimeOffset date)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, date.UtcDateTime);
            }
            else if (Directory.Exists(path))
            {
                Directory.SetLastWriteTimeUtc(path, date.UtcDateTime);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
        }
    }

    // MARK: - Move clean-up

    /// <summary>
    /// Checks every file a move wrote: present at the destination, and — where both sides know
    /// it — the size the source has. Returns the name of the first file that fails, or null.
    /// A local source is measured now, so a file that changed while it travelled fails too.
    /// </summary>
    private static async Task<string?> VerifyArrivalsAsync(TransferRequest request, IReadOnlyList<Arrival> arrivals, CancellationToken ct)
    {
        var source = request.Source;
        var destination = request.Destination;
        if (arrivals.Count == 0 || !destination.CanVerifyArrivals)
        {
            return null;
        }

        long Expected(Arrival arrival)
        {
            if (arrival.ExpectedSize < 0 || !source.IsLocal)
            {
                return arrival.ExpectedSize;
            }

            try
            {
                var info = new FileInfo(arrival.SourcePath);
                return info.Exists ? info.Length : -1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        }

        if (destination.IsLocal)
        {
            return await Task.Run(() =>
            {
                foreach (var arrival in arrivals)
                {
                    var info = new FileInfo(arrival.DestinationPath);
                    var expected = Expected(arrival);
                    if (!info.Exists || (expected >= 0 && info.Length != expected))
                    {
                        return Path.GetFileName(arrival.DestinationPath);
                    }
                }

                return null;
            }, ct).ConfigureAwait(false);
        }

        // One listing per destination folder rather than one metadata call per file.
        var matcher = MatcherFor(destination);
        foreach (var folder in arrivals.GroupBy(a => destination.GetParentPath(a.DestinationPath), StringComparer.Ordinal))
        {
            var listed = new Dictionary<string, CloudFileItem>(matcher);
            try
            {
                foreach (var child in await destination.ListDirectoryAsync(folder.Key, ct).ConfigureAwait(false))
                {
                    listed.TryAdd(child.Name, child);
                }
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
            {
            }

            foreach (var arrival in folder)
            {
                var name = destination.GetName(arrival.DestinationPath);
                if (!listed.TryGetValue(name, out var landed))
                {
                    // Listings can lag an upload by a moment; ask for the file itself.
                    try
                    {
                        landed = await destination.GetFileMetadataAsync(arrival.DestinationPath, ct).ConfigureAwait(false);
                    }
                    catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
                    {
                        return name;
                    }
                }

                var expected = Expected(arrival);
                if (landed.IsDirectory || (expected >= 0 && landed.Size >= 0 && landed.Size != expected))
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static async Task DeleteSourceAsync(FileProvider source, ItemPlan plan, IReadOnlyList<Arrival> arrivals, CancellationToken ct)
    {
        var item = plan.Item;
        if (!source.IsLocal)
        {
            await source.DeleteItemAsync(item.Path, ct).ConfigureAwait(false);
            return;
        }

        // Explorer deletes a moved source outright once the copy is complete; sending it to
        // the Recycle Bin would make a move to another drive leave a second copy behind.
        if (!item.IsDirectory || plan.Tree is null)
        {
            await Task.Run(() => LocalFileProvider.DeletePermanently(item.Path), ct).ConfigureAwait(false);
            return;
        }

        // A folder goes file by file — only what was copied — and then each folder that is left
        // empty. Something saved into the folder while it travelled stays where it is instead
        // of being deleted uncopied.
        await Task.Run(() =>
        {
            foreach (var arrival in arrivals)
            {
                try
                {
                    LocalFileProvider.DeletePermanently(arrival.SourcePath);
                }
                catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
                {
                }
            }

            var folders = plan.Tree
                .Where(e => e.Item.IsDirectory)
                .OrderByDescending(e => e.Key.Count(c => c == '/'))
                .Select(e => e.Item)
                .Append(item);
            foreach (var folder in folders)
            {
                TryRemoveEmptyDirectory(folder);
            }
        }, ct).ConfigureAwait(false);
    }

    private static void TryRemoveEmptyDirectory(CloudFileItem folder)
    {
        try
        {
            // A junction is removed as a link (its target is not this move's to delete); a real
            // folder only when nothing is left in it.
            if (folder.IsSymlink || !Directory.EnumerateFileSystemEntries(folder.Path).Any())
            {
                Directory.Delete(folder.Path, recursive: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // MARK: - Tree walking

    /// <summary>One file or folder under a top-level folder, keyed by its relative path.</summary>
    private sealed record TreeEntry(CloudFileItem Item, string Key, string ParentKey);

    private sealed class ItemPlan(CloudFileItem item, string targetName, bool replace, CloudFileItem? existing)
    {
        public CloudFileItem Item { get; } = item;

        public string TargetName { get; } = targetName;

        public bool Replace { get; } = replace;

        public CloudFileItem? Existing { get; } = existing;

        /// <summary>Folders and files beneath a top-level folder, parents before children.</summary>
        public List<TreeEntry>? Tree { get; set; }
    }

    private static async Task<List<TreeEntry>> WalkAsync(FileProvider provider, CloudFileItem root, CancellationToken ct)
    {
        var entries = new List<TreeEntry>();
        var pending = new Queue<(string Path, string Key)>();
        pending.Enqueue((root.Path, string.Empty));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, key) = pending.Dequeue();
            foreach (var child in await provider.ListDirectoryAsync(path, ct).ConfigureAwait(false))
            {
                // Junctions and symlinked folders are copied as what they point to only one
                // level deep — following them recursively can loop forever.
                var childKey = key.Length == 0 ? child.Name : key + "/" + child.Name;
                entries.Add(new TreeEntry(child, childKey, key));
                if (child.IsDirectory && !(provider.IsLocal && child.IsSymlink))
                {
                    pending.Enqueue((child.Path, childKey));
                }
            }
        }

        return entries;
    }

    private static async Task<IReadOnlyList<CloudFileItem>> ListOrEmptyAsync(FileProvider provider, string path, CancellationToken ct)
    {
        try
        {
            return await provider.ListDirectoryAsync(path, ct).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return [];
        }
    }

    private static bool IsSelfOrDescendant(FileProvider provider, CloudFileItem item, string destinationPath)
    {
        if (!item.IsDirectory)
        {
            return false;
        }

        if (provider.IsLocal)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));
            return target.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                   target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        return CloudPath.IsWithin(destinationPath, item.Path);
    }

    /// <summary>Whether <paramref name="path"/> sits somewhere below <paramref name="folder"/>.</summary>
    private static bool IsInside(FileProvider provider, string path, string folder)
    {
        if (provider.IsLocal)
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }

        return !PathsEqual(provider, path, folder) && CloudPath.IsWithin(path, folder);
    }

    private static bool PathsEqual(FileProvider provider, string a, string b)
        => provider.IsLocal
            ? string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase)
            : CloudPath.Normalize(a) == CloudPath.Normalize(b);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
