// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

// Debug builds only: Help → "Run Version Test…" must never reach end users (repository
// CLAUDE.md, "Version test (debug builds only)"). Nothing here is compiled into Release.
#if DEBUG

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using FileFluss.App.Views;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Settings;
using FileFluss.Native;

namespace FileFluss.App.Services;

/// <summary>
/// The macOS <c>VersionTestRunner</c>: exercises every chosen cloud account end to end with
/// the files in <c>Testfiles/</c> (create folder → upload → replace → share link → delete →
/// cleanup) and writes a Markdown report next to them. The engine lives in Core
/// (<see cref="VersionTestEngine"/>); this is the menu entry's UI around it. The macOS
/// runner's dialogs are English-only, and so are these.
/// </summary>
public static class VersionTestRunner
{
    private const string Title = "Version Test";

    private static VersionTestProgressWindow? _running;

    /// <summary>Help → "Run Version Test…". A second click while a run is going shows the run.</summary>
    public static async void Run(Window owner)
    {
        if (_running is not null)
        {
            _running.Activate();
            return;
        }

        try
        {
            await RunAsync(owner);
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Version test aborted — {e}", VersionTestEngine.LogCategory, LogLevel.Error);
            MessageDialog.Error(owner, Title, e.Message);
        }
    }

    private static async Task RunAsync(Window owner)
    {
        var services = AppServices.Current;
        var testfiles = LocateTestfiles();
        var localFiles = await Task.Run(() => VersionTestEngine.EnumerateLocalFiles(testfiles));
        if (localFiles.Count == 0)
        {
            var answer = MessageDialog.Show(owner, Title, $"Testfiles folder is empty:\n{testfiles}", DialogIcon.Warning,
                new DialogButton("close", "Close", IsCancel: true),
                new DialogButton("open", "Open Folder", IsDefault: true));
            if (answer == "open")
            {
                ShellFileOperations.Open(testfiles);
            }

            return;
        }

        var allAccounts = services.Accounts.Accounts.ToList();
        if (allAccounts.Count == 0)
        {
            MessageDialog.Info(owner, Title, "No cloud accounts connected.");
            return;
        }

        // A full pass over every account takes minutes; when chasing one provider's failure
        // you want just that provider, and remembering the choice makes a rerun two clicks.
        var accounts = SelectAccounts(owner, allAccounts, services.Settings);
        if (accounts is null || accounts.Count == 0)
        {
            return;
        }

        SupportLogger.Shared.Log($"Version test starting — {localFiles.Count} file(s), {accounts.Count} account(s)", VersionTestEngine.LogCategory, LogLevel.Notice);
        var startDate = DateTimeOffset.Now;
        var progress = new VersionTestProgressWindow(accounts.Count * VersionTestEngine.EstimatedStepsPerAccount(localFiles.Count)) { Owner = owner };
        _running = progress;
        progress.Show();

        List<VersionTestAccountResult> results;
        try
        {
            // Off the UI thread: providers do synchronous work in places (hashing, SSH), and
            // the progress window has to stay responsive for Cancel.
            results = await Task.Run(async () =>
            {
                var list = new List<VersionTestAccountResult>();
                for (var index = 0; index < accounts.Count; index++)
                {
                    var account = accounts[index];
                    progress.BeginAccount(account.DisplayName, index + 1, accounts.Count);
                    if (progress.IsCancelled)
                    {
                        break;
                    }

                    list.Add(await VersionTestEngine.RunAccountAsync(account, services.Hub.Get(account.Id), localFiles, progress).ConfigureAwait(false));
                }

                return list;
            });
        }
        finally
        {
            progress.Finish();
            _running = null;
        }

        // Some services rotate their tokens on every refresh: the saved set would be stale now.
        TestAccountsMenu.RefreshAfterVersionTest();

        var report = new VersionTestReport(startDate, DateTimeOffset.Now, testfiles, localFiles, results, UpdateService.CurrentVersion, BuildNumber());
        var reportPath = Path.Combine(testfiles, VersionTestEngine.ReportFileName(startDate));
        try
        {
            await File.WriteAllTextAsync(reportPath, VersionTestEngine.RenderMarkdown(report), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SupportLogger.Shared.Log("Version test complete, but report write failed", VersionTestEngine.LogCategory, LogLevel.Error);
            MessageDialog.Error(owner, Title, $"The report could not be written to {reportPath}: {e.Message}");
            return;
        }

        SupportLogger.Shared.Log($"Version test complete — report at {reportPath}", VersionTestEngine.LogCategory, LogLevel.Notice);
        ShowCompletion(owner, report, reportPath);
    }

    /// <summary>
    /// macOS reads <c>Testfiles/</c> beside the project source (<c>#filePath</c>). Here: the
    /// Testfiles folder of the checkout the build runs from, else
    /// <c>Testfiles</c> in the debug build's data folder, created so there is a place to put files.
    /// </summary>
    private static string LocateTestfiles()
    {
        if (VersionTestEngine.FindRepositoryTestfiles(AppContext.BaseDirectory) is { } inRepository)
        {
            return inRepository;
        }

        var fallback = Path.Combine(SettingsStore.DataDirectory, "Testfiles");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string BuildNumber()
    {
        var informational = UpdateService.InformationalVersion;
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[(plus + 1)..] : "?";
    }

    /// <summary>The account checklist. Skipped with a single account — nothing to choose. Null when cancelled.</summary>
    private static List<CloudAccount>? SelectAccounts(Window owner, List<CloudAccount> accounts, AppSettings settings)
    {
        if (accounts.Count <= 1)
        {
            return accounts;
        }

        var chosen = VersionTestAccountDialog.Ask(owner, accounts, settings.VersionTestAccountIds.ToHashSet());
        if (chosen is null)
        {
            return null;
        }

        settings.VersionTestAccountIds = chosen.Select(a => a.Id).ToList();
        if (chosen.Count == 0)
        {
            MessageDialog.Info(owner, Title, "No accounts selected.");
        }

        return chosen;
    }
    private static void ShowCompletion(Window owner, VersionTestReport report, string reportPath)
    {
        var failures = report.TotalCount - report.PassCount;
        var answer = MessageDialog.Show(
            owner,
            failures == 0 ? "Version Test — All Passed" : $"Version Test — {failures} Failure(s)",
            $"{report.PassCount} of {report.TotalCount} steps passed.\n\nReport:\n{Path.GetFileName(reportPath)}",
            failures == 0 ? DialogIcon.Info : DialogIcon.Warning,
            new DialogButton("close", "Close", IsCancel: true),
            new DialogButton("reveal", "Show in Explorer"),
            new DialogButton("open", "Open Report", IsDefault: true));
        switch (answer)
        {
            case "open":
                OpenReport(reportPath);
                break;
            case "reveal":
                ShellFileOperations.RevealInExplorer([reportPath]);
                break;
        }
    }

    /// <summary>The .md handler if Windows has one, else Notepad (a fresh Windows has none for .md).</summary>
    private static void OpenReport(string path)
    {
        try
        {
            ShellFileOperations.Open(path);
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }
}

#endif
