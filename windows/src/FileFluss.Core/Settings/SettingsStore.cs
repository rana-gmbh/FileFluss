// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileFluss.Core.IO;

namespace FileFluss.Core.Settings;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> as JSON (ported from the NetFluss Windows
/// store). Takes an explicit path so tests never touch the real settings.
///
/// <para><b>Corrupt files never cost the user their settings.</b> An unreadable file is
/// copied aside as <c>settings.json.unreadable-*.json</c> before anything can save defaults
/// over it, and the app starts with defaults.</para>
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new LenientEnumConverterFactory() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Timer _saveTimer;
    private bool _suspended;
    private bool _unread;
    private int _unsaved;

    public SettingsStore(string path)
    {
        _path = path;
        Settings = Load();
        Settings.PropertyChanged += OnSettingsChanged;
        _saveTimer = new Timer(_ => SaveIfUnsaved());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>
    /// How long changes are collected before they are written. Settings change on the UI
    /// thread, often several in a row (a window closing, a column drag); writing each one there
    /// cost a JSON serialisation and a file replace — 50 ms the first time.
    /// </summary>
    public static TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public AppSettings Settings { get; }

    /// <summary>Raised after a change, on the thread that made it; the write follows within <see cref="SaveDelay"/>, off that thread.</summary>
    public event EventHandler<string?>? Changed;

    /// <summary>%LOCALAPPDATA%\FileFluss — settings, accounts, the index and logs live here.</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileFluss");

    public static string DefaultPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Several changes, one write.</summary>
    public void Batch(Action<AppSettings> edit)
    {
        _suspended = true;
        try
        {
            edit(Settings);
        }
        finally
        {
            _suspended = false;
        }

        ScheduleSave();
        Changed?.Invoke(this, null);
    }

    /// <summary>Writes pending changes now (the app calls this as it exits; it also runs at process exit).</summary>
    public void Flush()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveIfUnsaved();
    }

    private void ScheduleSave()
    {
        Volatile.Write(ref _unsaved, 1);
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>On the timer's thread: AppSettings replaces its collections rather than changing them, so reading it there is safe.</summary>
    private void SaveIfUnsaved()
    {
        if (Interlocked.Exchange(ref _unsaved, 0) == 0)
        {
            return;
        }

        try
        {
            Save();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            // A collection changed under the serializer after all: try again shortly.
            ScheduleSave();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                if (_unread)
                {
                    // The file couldn't be read at launch: keep a copy before defaults replace it.
                    Preserve();
                    _unread = false;
                }

                // Durable write-then-replace, keeping the previous file as settings.json.bak: a
                // crash or power cut midway leaves a complete file either way.
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Settings, SerializerOptions));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A locked profile costs persistence for this session, nothing else.
            }
        }
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path) && !File.Exists(AtomicFile.BackupPath(_path)))
        {
            return new AppSettings();
        }

        string? text = null;
        for (var attempt = 0; attempt < 5 && text is null; attempt++)
        {
            try
            {
                text = File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A scanner or backup tool holding the file at launch: wait it out briefly.
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        if (text is null)
        {
            _unread = true;
        }
        else if (text.Length > 0)
        {
            if (TryDeserialize(text) is { } settings)
            {
                return settings;
            }

            Preserve();
        }

        // Damaged, empty (a power cut's leftover) or unreadable: the previous good file.
        try
        {
            var backup = AtomicFile.BackupPath(_path);
            if (File.Exists(backup) && TryDeserialize(File.ReadAllText(backup)) is { } previous)
            {
                return previous;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return new AppSettings();
    }

    private static AppSettings? TryDeserialize(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(text, SerializerOptions);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Preserve()
    {
        try
        {
            File.Copy(_path, $"{_path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suspended)
        {
            return;
        }

        ScheduleSave();
        Changed?.Invoke(this, e.PropertyName);
    }
}
