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
    private bool _suspended;

    public SettingsStore(string path)
    {
        _path = path;
        Settings = Load();
        Settings.PropertyChanged += OnSettingsChanged;
    }

    public AppSettings Settings { get; }

    /// <summary>Raised after a change has been written.</summary>
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

        Save();
        Changed?.Invoke(this, null);
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

        Save();
        Changed?.Invoke(this, e.PropertyName);
    }
}
