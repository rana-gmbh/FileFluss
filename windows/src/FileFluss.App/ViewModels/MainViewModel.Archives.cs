// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows.Input;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Archives;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.ViewModels;

/// <summary>
/// Archives in the panels (macOS 1.6 "Archives, right in the panel"): opening one like a
/// folder, opening and previewing what is inside, copying entries out, Extract Here / Extract
/// to … Panel for a whole archive, Compress, and fetching RARLAB's rar tool.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Opens the archive <paramref name="item"/> in <paramref name="panel"/> — on this PC
    /// directly, on an account once it is downloaded to the open cache (where a second visit
    /// finds it). Returns false when the file can't be read as an archive after all (an
    /// extension that lies, or an empty listing), so the caller opens it the ordinary way.
    /// </summary>
    private async Task<bool> TryOpenArchiveAsync(PanelViewModel panel, CloudFileItem item, ArchiveFormat format)
    {
        string localFile;
        Guid? accountId = null;
        if (panel.IsLocal)
        {
            localFile = item.Path;
        }
        else if (panel.IsCloud && !panel.IsOffline && panel.Location.AccountId is { } id)
        {
            accountId = id;
            if (await OpenCloudFileAsync(panel, item, launch: false) is not { } cached)
            {
                // Couldn't be downloaded: OpenCloudFileAsync has said why.
                return true;
            }

            localFile = cached;
        }
        else
        {
            return false;
        }

        var provider = _services.Archives.Open(accountId, item.Path, localFile, format, item.Name);
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.AppStarting;
        ArchiveTree tree;
        try
        {
            tree = await provider.TreeAsync();
        }
        catch (ArchiveException e) when (e.Kind == ArchiveErrorKind.Unreadable)
        {
            // Not an archive worth an error: the file goes to its own app.
            SupportLogger.Shared.Log($"{item.Name} isn't readable as an archive ({e.Message}); opening it instead", "archive");
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            MessageDialog.Error(Shell.Window, L("Archive failed"), e.Message);
            return true;
        }
        finally
        {
            Mouse.OverrideCursor = cursor;
        }

        // An archive that lists nothing is nearly always a file whose extension lies. An empty
        // panel would be a worse answer than letting the file open in its own app.
        if (tree.IsEmpty)
        {
            return false;
        }

        SupportLogger.Shared.Log($"opened archive {item.Name} — {tree.Entries.Count} entries, format {format}", "archive");
        await panel.NavigateAsync(PanelLocation.Archive(item.Path, accountId));
        Shell.FocusPanel(panel.Side);
        return true;
    }

    /// <summary>
    /// Opens one entry with its own app, unpacking just it. An archive inside an archive opens
    /// the same way rather than in the panel, as on macOS: its breadcrumb would have to show
    /// the staging folder it was unpacked to.
    /// </summary>
    private async Task OpenArchiveEntryAsync(PanelViewModel panel, ArchiveProvider archive, CloudFileItem item)
    {
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var opened = await archive.ExtractForOpeningAsync([item.Path]);
            if (opened.FirstOrDefault() is not { } path)
            {
                return;
            }

            // Out of a downloaded archive: SmartScreen and Protected View apply, as to the
            // archive's other downloads.
            if (panel.Location.ArchiveAccountId is not null)
            {
                MarkOfTheWeb.Apply(path);
            }

            ShellFileOperations.Open(path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", item.Name), e.Message);
        }
        finally
        {
            Mouse.OverrideCursor = cursor;
        }
    }

    /// <summary>Unpacks an entry for Quick Look, into the archive's staging folder. Null when it failed.</summary>
    public async Task<string?> PreviewArchiveEntryAsync(ArchiveProvider archive, CloudFileItem item)
    {
        try
        {
            return (await archive.MaterializeAsync([item.Path])).FirstOrDefault();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SupportLogger.Shared.Log($"Quick Look couldn't unpack {item.Name}: {e.Message}", "archive", LogLevel.Notice);
            return null;
        }
    }

    /// <summary>
    /// A copy out of an archive (F5, drag, "Extract to … Panel"): an ordinary transfer, listed
    /// as "Extracting". For a tarball or a solid archive the selection is unpacked in one pass
    /// first, and the transfer copies the unpacked files; the archive's staging folder is
    /// leased for as long as the transfer runs.
    /// </summary>
    private void StartArchiveCopy(ArchiveProvider archive, TransferRequest request, PanelSide panel, Action<TransferProgress> onFinished)
    {
        var progress = new TransferProgress(TransferOperation.Extracting, request.Items.Count, panel)
        {
            IsCloudToCloud = !request.Destination.IsLocal,
        };
        progress.SetItems(request.Items);
        _services.Transfers.Track(progress, onFinished);
        _ = Task.Run(async () =>
        {
            using var lease = archive.Lease();
            try
            {
                await archive.PrepareForTransferAsync(request.Items, progress.CancellationToken);
            }
            catch (OperationCanceledException) when (progress.CancellationToken.IsCancellationRequested)
            {
                progress.Complete();
                return;
            }
            catch (Exception e)
            {
                progress.Fail(e.Message);
                progress.Complete();
                return;
            }

            await TransferEngine.RunAsync(request, progress);
        });
    }

    /// <summary>"Extract to … Panel" for entries inside an archive: a copy to the other panel.</summary>
    public Task ExtractSelectionToOtherPanelAsync(PanelViewModel panel)
        => TransferAsync(panel.Provider, panel.SelectedItems, Panel(panel.Side.Other()).Location, false, panel.Side.Other());

    /// <summary>
    /// "Extract Here" (<paramref name="destination"/> is the archive's own panel) or "Extract to
    /// … Panel" for a whole archive, by Finder's rule. On an account the archive is downloaded,
    /// unpacked, and the result uploaded beside it.
    /// </summary>
    public void ExtractArchive(PanelViewModel panel, CloudFileItem archive, PanelViewModel destination)
    {
        if (panel.Provider is not { } source || destination.Provider is not { } target)
        {
            MessageDialog.Error(Shell.Window, L("Archive failed"), L("Cloud account not connected"));
            return;
        }

        if (destination.IsReadOnly || (destination.IsLocal && destination.CurrentPath.Length == 0))
        {
            MessageDialog.Error(Shell.Window, L("Archive failed"), L("This location is read-only."));
            return;
        }

        var progress = ArchiveJobs.CreateProgress(TransferOperation.Extracting, archive.Name, destination.Side);
        var folder = destination.CurrentPath;
        _services.Transfers.Track(progress, finished => ArchiveJobFinished(finished, source, destination.Location));
        _ = Task.Run(() => ArchiveJobs.ExtractAsync(source, archive, target, folder, progress));
    }

    /// <summary>Compress › format: packs the selection beside it, as "&lt;name&gt;.&lt;ext&gt;" or "Archive.&lt;ext&gt;".</summary>
    public void Compress(PanelViewModel panel, IReadOnlyList<CloudFileItem> items, ArchiveCreationFormat format)
    {
        if (items.Count == 0 || panel.Provider is not { } provider || panel.IsReadOnly || (panel.IsLocal && panel.CurrentPath.Length == 0))
        {
            return;
        }

        if (!format.IsAvailable())
        {
            if (format == ArchiveCreationFormat.Rar)
            {
                OfferRarInstall();
            }

            return;
        }

        var taken = panel.AllRows.Select(r => r.Name).ToHashSet(provider.NameComparer);
        var name = format.ArchiveName(items.Select(i => i.Name).ToList(), taken.Contains);
        var progress = ArchiveJobs.CreateProgress(TransferOperation.Compressing, name, panel.Side);
        var folder = panel.CurrentPath;
        _services.Transfers.Track(progress, finished => ArchiveJobFinished(finished, provider, panel.Location));
        _ = Task.Run(() => ArchiveJobs.CompressAsync(provider, folder, items, name, format, progress));
    }

    /// <summary>A compress or extract job ended: panels showing its folder list again, and a failure is said out loud.</summary>
    private void ArchiveJobFinished(TransferProgress progress, FileProvider source, PanelLocation destination)
    {
        RefreshAfterTransfer(source, destination);
        foreach (var panel in new[] { Left, Right })
        {
            if (panel.Location == destination)
            {
                _ = panel.RefreshAsync(quiet: true);
            }
        }

        if (!progress.IsCancelled && progress.SuccessCount == 0 && progress.Results.FirstOrDefault(r => r.Status == TransferItemStatus.Failed) is { } failure)
        {
            MessageDialog.Error(Shell.Window, L("Archive failed"), failure.ErrorMessage ?? L("Unknown error"));
        }
    }

    /// <summary>"Show in Explorer" inside an archive on this PC: the archive itself, as its entries have no path Explorer knows.</summary>
    public void RevealArchive(PanelViewModel panel)
    {
        if (panel.Location is { IsArchive: true, ArchiveAccountId: null, ArchivePath: { } path })
        {
            ShellFileOperations.RevealInExplorer([path]);
        }
    }

    // MARK: - The rar tool

    private bool _isInstallingRar;

    /// <summary>
    /// The offer to fetch RARLAB's rar tool, and what came of it (macOS <c>RarInstallPrompt</c>).
    /// On Windows RARLAB ships the console tool only inside the WinRAR installer, so that is
    /// what is downloaded, checked against its pinned fingerprint and read — never run.
    /// </summary>
    public void OfferRarInstall()
    {
        if (_isInstallingRar)
        {
            return;
        }

        var answer = MessageDialog.Show(
            Shell.Window,
            L("Install the rar tool?"),
            L("FileFluss can download WinRAR {0} from rarlab.com (about 4 MB), check it against a known fingerprint, and keep only its command-line rar tool in its own folder. The installer is never run, and no administrator rights are needed.\n\nRAR is trial software from RARLAB: it may be used for up to 40 days, after which a licence has to be bought from rarlab.com.", RarTool.Version),
            DialogIcon.Question,
            new DialogButton("cancel", L("Cancel"), IsCancel: true),
            new DialogButton("site", L("Open rarlab.com")),
            new DialogButton("install", L("Download and Install"), IsDefault: true));
        switch (answer)
        {
            case "site":
                BrowserLauncher.Open(RarTool.DownloadPage);
                break;
            case "install":
                _ = InstallRarAsync();
                break;
        }
    }

    private async Task InstallRarAsync()
    {
        _isInstallingRar = true;
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.AppStarting;
        string? failure = null;
        try
        {
            await Task.Run(() => RarTool.InstallAsync());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SupportLogger.Shared.Log($"rar install failed: {e.Message}", "archive", LogLevel.Error);
            failure = e.Message;
        }
        finally
        {
            Mouse.OverrideCursor = cursor;
            _isInstallingRar = false;
        }

        if (failure is null)
        {
            MessageDialog.Info(Shell.Window, L("The rar tool is installed"), L("Choose Compress › RAR to make RAR archives."));
            return;
        }

        var answer = MessageDialog.Show(
            Shell.Window,
            L("The rar tool could not be installed"),
            failure,
            DialogIcon.Error,
            new DialogButton("site", L("Open rarlab.com")),
            new DialogButton("ok", L("OK"), IsDefault: true, IsCancel: true));
        if (answer == "site")
        {
            BrowserLauncher.Open(RarTool.DownloadPage);
        }
    }

    /// <summary>Where a panel inside an archive is remembered: the folder the archive sits in (its own path is a virtual one).</summary>
    private static PanelLocation Rememberable(PanelLocation location) => location.ArchiveContainer ?? location;
}
