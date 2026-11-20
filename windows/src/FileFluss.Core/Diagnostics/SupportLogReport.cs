// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text;

namespace FileFluss.Core.Diagnostics;

/// <summary>What the support log's header says about the machine it was recorded on.</summary>
public sealed record SupportLogEnvironment(
    string AppVersion,
    string Build,
    string OperatingSystem,
    string Architecture,
    string Locale,
    string Hardware);

/// <summary>
/// The text of a "Support Log" recording — the macOS <c>SupportLogService.collectReport</c>
/// format, with the header's macOS and hardware lines adapted to Windows: a header block,
/// then one line per captured event.
/// </summary>
public static class SupportLogReport
{
    /// <summary>How long a recording runs (macOS <c>recordingDuration</c>).</summary>
    public const int RecordingSeconds = 60;

    public static string Build(SupportLogEnvironment environment, IReadOnlyList<SupportLogger.LogEntry> entries, DateTimeOffset generated)
    {
        var output = new StringBuilder();
        output.Append("FileFluss Support Log\n");
        output.Append("=====================\n");
        output.Append(CultureInfo.InvariantCulture, $"Generated: {generated.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\n");
        output.Append(CultureInfo.InvariantCulture, $"App version: {environment.AppVersion} (build {environment.Build})\n");
        output.Append(CultureInfo.InvariantCulture, $"Windows: {environment.OperatingSystem}\n");
        output.Append(CultureInfo.InvariantCulture, $"Architecture: {environment.Architecture}\n");
        output.Append(CultureInfo.InvariantCulture, $"Locale: {environment.Locale}\n");
        output.Append(CultureInfo.InvariantCulture, $"Hardware: {environment.Hardware}");
        output.Append("\n\n--- Events captured ---\n");
        if (entries.Count == 0)
        {
            output.Append("(no events captured during this window)\n");
            return output.ToString();
        }

        foreach (var entry in entries)
        {
            output.Append(CultureInfo.InvariantCulture, $"{entry.Date.ToLocalTime():HH:mm:ss.fff} [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Category}] {entry.Message}\n");
        }

        return output.ToString();
    }

    /// <summary>"FileFluss-support-20261005-142233.log", as the macOS save panel suggests.</summary>
    public static string FileName(DateTimeOffset now)
        => string.Create(CultureInfo.InvariantCulture, $"FileFluss-support-{now.ToLocalTime():yyyyMMdd-HHmmss}.log");
}
