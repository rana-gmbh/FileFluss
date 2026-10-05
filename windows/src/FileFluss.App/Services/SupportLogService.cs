// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Diagnostics;
using Microsoft.Win32;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Services;

/// <summary>
/// Help → "Support Log": records a 60-second window of support events, then asks where to
/// save them for a bug report — the macOS <c>SupportLogService</c>. The events come from the
/// always-on <see cref="SupportLogger"/>; this only marks the window and writes the file.
/// </summary>
public sealed class SupportLogService
{
    private DispatcherTimer? _timer;
    private DateTimeOffset? _start;

    public static SupportLogService Shared { get; } = new();

    /// <summary>Raised every second while recording, and when recording starts and stops.</summary>
    public event EventHandler? Changed;

    public bool IsRecording { get; private set; }

    public int SecondsRemaining { get; private set; }

    /// <summary>The window the save dialog belongs to.</summary>
    public Window? Owner { get; set; }

    public void Start()
    {
        if (IsRecording)
        {
            return;
        }

        IsRecording = true;
        _start = DateTimeOffset.Now;
        SecondsRemaining = SupportLogReport.RecordingSeconds;
        SupportLogger.Shared.Log("Support log recording started", "support", LogLevel.Notice);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Tick()
    {
        SecondsRemaining--;
        if (SecondsRemaining <= 0)
        {
            Finish();
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Finish()
    {
        if (_start is not { } start)
        {
            Reset();
            return;
        }

        SupportLogger.Shared.Log("Support log recording stopped", "support", LogLevel.Notice);
        var now = DateTimeOffset.Now;
        var report = SupportLogReport.Build(Environment(), SupportLogger.Shared.Snapshot(start), now);
        Reset();
        Save(report, now);
    }

    private void Reset()
    {
        _timer?.Stop();
        _timer = null;
        _start = null;
        SecondsRemaining = 0;
        IsRecording = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save(string report, DateTimeOffset now)
    {
        var dialog = new SaveFileDialog
        {
            Title = L("Save FileFluss Support Log"),
            FileName = SupportLogReport.FileName(now),
            DefaultExt = ".log",
            AddExtension = true,
            Filter = L("Support Log") + " (*.log)|*.log|" + L("All files") + "|*.*",
            InitialDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory),
        };
        var owner = Owner ?? Application.Current.MainWindow;
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, report, new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(owner, L("Save FileFluss Support Log"), e.Message);
        }
    }

    /// <summary>The macOS header's version, OS, locale and hardware lines, as Windows knows them.</summary>
    private static SupportLogEnvironment Environment()
    {
        var informational = UpdateService.InformationalVersion;
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var build = plus >= 0 ? informational[(plus + 1)..] : "?";
        return new SupportLogEnvironment(
            AppVersion: UpdateService.CurrentVersion,
            Build: build,
            OperatingSystem: $"{RuntimeInformation.OSDescription.Trim()} ({WindowsDisplayVersion()})",
            Architecture: $"{RuntimeInformation.OSArchitecture} (process {RuntimeInformation.ProcessArchitecture})",
            Locale: $"{CultureInfo.CurrentCulture.Name} (UI {CultureInfo.CurrentUICulture.Name}, app {Current})",
            Hardware: HardwareModel());
    }

    /// <summary>"24H2", as Settings → System → About shows it.</summary>
    private static string WindowsDisplayVersion()
        => ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? "?";

    /// <summary>The Windows counterpart of the macOS <c>hw.model</c>: the firmware's maker and model.</summary>
    private static string HardwareModel()
    {
        var maker = ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer");
        var model = ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
        var text = string.Join(" ", new[] { maker, model }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        return text.Length == 0 ? "?" : $"{text}, {System.Environment.ProcessorCount} logical processors";
    }

    private static string? ReadRegistry(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
