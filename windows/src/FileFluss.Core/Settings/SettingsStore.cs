// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

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
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write-then-replace: a crash midway leaves the previous file intact.
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, SerializerOptions));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A locked profile costs persistence for this session, nothing else.
            }
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), SerializerOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            Preserve();
            return new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
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
