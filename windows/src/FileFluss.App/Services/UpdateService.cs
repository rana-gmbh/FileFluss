// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Net;
using FileFluss.Core.Settings;
using FileFluss.Core.Updates;
using Microsoft.Win32;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Services;

/// <summary>
/// In-app updates — the Windows counterpart of the macOS <c>AppUpdater</c> (Sparkle 2), ported
/// from the NetFluss Windows updater. Asks GitHub for the newest <c>win-v*</c> release; for a
/// copy installed by the setup it downloads this architecture's installer, checks it against
/// the release's SHA256SUMS.txt, runs it silently and lets it relaunch FileFluss. A portable
/// copy (the zip) or a development build is pointed at the release page instead.
///
/// <para>The automatic check runs at most once a day and honours
/// <see cref="AppSettings.AutomaticUpdateChecks"/> (macOS <c>automaticUpdateChecksEnabled</c>)
/// and <see cref="AppSettings.LastUpdateCheck"/>. Failures of an automatic check stay
/// silent and are retried an hour later.</para>
/// </summary>
public sealed class UpdateService
{
    /// <summary>Matches AppId in windows/Packaging/FileFluss.iss.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ED875F07-FD68-4091-AD98-B37CDC7B8490}_is1";

    /// <summary>"Skip This Version", per version, in <see cref="AppSettings.Flags"/>.</summary>
    private const string SkippedFlagPrefix = "updates.skipped.";

    private const int ReleaseNotesLimit = 700;

    private readonly AppServices _services;
    private DispatcherTimer? _timer;
    private Window? _owner;
    private bool _busy;

    public UpdateService(AppServices services)
    {
        _services = services;
        _ = Task.Run(RemoveFinishedDownloads);
    }

    /// <summary>Raised when any check found a release newer than the one running.</summary>
    public event EventHandler<AvailableUpdate>? UpdateFound;

    /// <summary>The newest release the last successful check found, if newer than this one.</summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>The full informational version, with the source revision after a "+" where the build added one.</summary>
    public static string InformationalVersion { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The running version ("1.2.3"), as build-release.ps1 stamps it with -p:Version.</summary>
    public static string CurrentVersion { get; } = InformationalVersion.Split('+')[0];

    /// <summary>Installers are downloaded here, not to %TEMP%, and removed once the update is in.</summary>
    private static string DownloadsFolder => Path.Combine(SettingsStore.DataDirectory, "Updates");

    private AppSettings Settings => _services.Settings;

    /// <summary>
    /// Whether this copy came from the installer, so a setup can replace it: the setup's
    /// uninstall entry must point at the folder this executable runs from.
    /// </summary>
    public static bool IsInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location) &&
                   string.Equals(Path.GetFullPath(location).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// <para><b>interactive: true</b> — Help → "Check for Updates…": checks now, whatever the
    /// schedule or the automatic-check switch, and always answers: the offer, "up to date",
    /// or the error.</para>
    /// <para><b>interactive: false</b> — the startup check: runs only when
    /// <see cref="UpdateLookup.IsDue"/> says so, stays silent unless there is a newer
    /// version the user hasn't skipped, and keeps re-evaluating hourly while the app runs,
    /// so a FileFluss left open for a week still checks once a day.</para>
    /// </summary>
    public async Task CheckAsync(bool interactive, Window owner)
    {
        _owner = owner;
        if (!interactive)
        {
            StartSchedule();
            await CheckIfDueAsync();
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        AvailableUpdate? update;
        try
        {
            update = await FetchAsync();
        }
        catch (Exception e) when (IsCheckFailure(e))
        {
            SupportLogger.Shared.Log($"Update check failed: {e.Message}", "Updates", LogLevel.Notice);
            MessageDialog.Error(owner, L("Check for Updates"), L("FileFluss could not check for updates: {0}", e.Message));
            return;
        }
        finally
        {
            _busy = false;
        }

        if (update is null)
        {
            MessageDialog.Info(owner, L("You're up to date!"), L("FileFluss {0} is currently the newest version available.", CurrentVersion));
            return;
        }

        await OfferAsync(update, automatic: false, owner);
    }

    private void StartSchedule()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(1) };
        _timer.Tick += async (_, _) => await CheckIfDueAsync();
        _timer.Start();
    }

    private async Task CheckIfDueAsync()
    {
        if (_busy || !UpdateLookup.IsDue(Settings.AutomaticUpdateChecks, Settings.LastUpdateCheck, DateTimeOffset.Now))
        {
            return;
        }

        _busy = true;
        AvailableUpdate? update;
        try
        {
            update = await FetchAsync();
        }
        catch (Exception e) when (IsCheckFailure(e))
        {
            // Offline, rate-limited or GitHub down: LastUpdateCheck stays put, so the next
            // hourly tick tries again. Nothing is shown for a check the user didn't ask for.
            SupportLogger.Shared.Log($"Automatic update check failed: {e.Message}", "Updates", LogLevel.Notice);
            return;
        }
        finally
        {
            _busy = false;
        }

        if (update is not null && !Settings.GetFlag(SkippedFlagPrefix + update.Version))
        {
            await OfferAsync(update, automatic: true, OwnerWindow());
        }
    }

    private async Task<AvailableUpdate?> FetchAsync()
    {
        var update = await UpdateLookup.FetchAsync(CurrentVersion);
        Settings.LastUpdateCheck = DateTimeOffset.Now;
        Available = update;
        SupportLogger.Shared.Log(update is null ? $"Update check: {CurrentVersion} is current" : $"Update check: {update.Version} is available", "Updates");
        if (update is not null)
        {
            UpdateFound?.Invoke(this, update);
        }

        return update;
    }

    /// <summary>Sparkle's "A new version is available" — Install / Skip This Version / Later.</summary>
    private async Task OfferAsync(AvailableUpdate update, bool automatic, Window? owner)
    {
        var installable = update.Installer is not null && update.Checksums is not null && IsInstalledCopy();

        var message = L("FileFluss {0} is available — you have {1}.", update.Version, CurrentVersion);
        var notes = update.ReleaseNotes.Trim();
        if (notes.Length > 0)
        {
            message += "\n\n" + (notes.Length > ReleaseNotesLimit ? notes[..ReleaseNotesLimit].TrimEnd() + " …" : notes);
        }

        if (!installable)
        {
            message += "\n\n" + L("This copy of FileFluss was not installed with the FileFluss setup, so it can't update itself. Download the new version from the release page.");
        }

        var buttons = new List<DialogButton>();
        if (automatic)
        {
            buttons.Add(new DialogButton("skip", L("Skip This Version")));
        }

        buttons.Add(new DialogButton("later", L("Later"), IsCancel: true));
        buttons.Add(installable
            ? new DialogButton("install", L("Install and Relaunch"), IsDefault: true)
            : new DialogButton("download", L("Download"), IsDefault: true));

        switch (MessageDialog.Show(owner, L("FileFluss {0} is available", update.Version), message, DialogIcon.Info, [.. buttons]))
        {
            case "skip":
                Settings.SetFlag(SkippedFlagPrefix + update.Version, true);
                break;
            case "download":
                BrowserLauncher.Open(update.ReleasePage);
                break;
            case "install":
                await InstallAsync(update, owner);
                break;
        }
    }

    /// <summary>
    /// Downloads and verifies the setup, then closes FileFluss the normal way — which asks
    /// first if transfers are running — and only once it is closing starts the setup, which
    /// installs silently and relaunches FileFluss (/LAUNCH, see FileFluss.iss).
    /// </summary>
    private async Task InstallAsync(AvailableUpdate update, Window? owner)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        string? installer = null;
        string? failure = null;
        var progress = new DownloadProgressWindow(update.Version) { Owner = owner is { IsLoaded: true } ? owner : null };
        try
        {
            progress.Show();
            installer = await UpdateLookup.DownloadVerifiedInstallerAsync(
                update,
                Path.Combine(DownloadsFolder, update.Version),
                new Progress<double>(progress.Report),
                cancellation: progress.Cancellation);
        }
        catch (OperationCanceledException) when (progress.Cancellation.IsCancellationRequested)
        {
            // Cancelled from the progress window.
        }
        catch (UpdateVerificationException e)
        {
            SupportLogger.Shared.Log($"Update {update.Version} refused: {e.Message}", "Updates", LogLevel.Error);
            failure = L("The update could not be verified, so it was not installed.");
        }
        catch (Exception e) when (IsCheckFailure(e) || e is IOException or UnauthorizedAccessException)
        {
            SupportLogger.Shared.Log($"Update {update.Version} download failed: {e.Message}", "Updates", LogLevel.Error);
            failure = L("The update could not be downloaded: {0}", e.Message);
        }
        finally
        {
            _busy = false;
            progress.Dismiss();
        }

        if (failure is not null)
        {
            OfferReleasePage(owner, update, failure);
            return;
        }

        if (installer is null)
        {
            return;
        }

        var main = Application.Current.MainWindow;
        var closed = false;
        if (main is not null)
        {
            main.Closed += (_, _) => closed = true;
            main.Close();
            if (!closed)
            {
                // The user chose to keep running transfers; the verified setup stays for next time.
                return;
            }
        }

        try
        {
            SupportLogger.Shared.Log($"Starting the {update.Version} setup", "Updates");
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LAUNCH",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(installer)!,
            })?.Dispose();
        }
        catch (Win32Exception e)
        {
            SupportLogger.Shared.Log($"The {update.Version} setup could not be started: {e.Message}", "Updates", LogLevel.Error);
        }

        Application.Current.Shutdown();
    }

    private static void OfferReleasePage(Window? owner, AvailableUpdate update, string message)
    {
        if (MessageDialog.Show(owner, L("Check for Updates"), message, DialogIcon.Warning,
                new DialogButton("close", L("Close"), IsCancel: true),
                new DialogButton("open", L("Open Release Page"), IsDefault: true)) == "open")
        {
            BrowserLauncher.Open(update.ReleasePage);
        }
    }

    private Window? OwnerWindow()
        => _owner is { IsLoaded: true, IsVisible: true } ? _owner : Application.Current.MainWindow;

    private static bool IsCheckFailure(Exception e)
        => e is HttpRequestException or TaskCanceledException or JsonException;

    /// <summary>Setups for versions that are now installed (or older) have done their job.</summary>
    private static void RemoveFinishedDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadsFolder))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(DownloadsFolder))
            {
                if (!UpdateLookup.IsNewer(Path.GetFileName(folder), CurrentVersion))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A small modeless window: the download's progress and a Cancel button.</summary>
    private sealed class DownloadProgressWindow : Window
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 16) };
        private bool _closed;

        public DownloadProgressWindow(string version)
        {
            Title = L("Check for Updates");
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var cancel = new Button { Content = L("Cancel"), IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
            cancel.Click += (_, _) => Close();

            var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            stack.Children.Add(new TextBlock { Text = L("Downloading FileFluss {0}…", version), TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(_bar);
            stack.Children.Add(cancel);
            Content = stack;
            Closing += (_, _) => _cancellation.Cancel();
            Closed += (_, _) => _closed = true;
        }

        public CancellationToken Cancellation => _cancellation.Token;

        /// <summary>Closes the window if the user hasn't already.</summary>
        public void Dismiss()
        {
            if (!_closed)
            {
                Close();
            }
        }

        public void Report(double fraction)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = fraction;
        }
    }
}
