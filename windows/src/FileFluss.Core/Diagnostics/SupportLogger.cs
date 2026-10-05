// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Diagnostics;

public enum LogLevel
{
    Info,
    Notice,
    Error,
}

/// <summary>
/// Always-on, in-memory ring buffer of file and cloud operation events — the macOS
/// <c>SupportLogger</c>. Cheap to call from any thread. "Save Support Log…" snapshots the
/// entries inside the recording window. Never log tokens, passwords or response bodies that
/// carry them.
/// </summary>
public sealed class SupportLogger
{
    public static readonly SupportLogger Shared = new();

    private const int MaxEntries = 5000;
    private readonly Lock _gate = new();
    private readonly Queue<LogEntry> _entries = new();

    public sealed record LogEntry(DateTimeOffset Date, LogLevel Level, string Category, string Message);

    /// <summary>Raised for every entry, for an optional file sink. Called on the logging thread.</summary>
    public event Action<LogEntry>? EntryAdded;

    public void Log(string message, string category = "General", LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot(DateTimeOffset since)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Date >= since).ToList();
        }
    }
}
