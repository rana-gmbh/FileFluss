// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;

namespace FileFluss.Core.Services;

/// <summary>
/// The list of connected accounts (macOS <c>UserDefaults["cloudAccounts"]</c>), as JSON in
/// <c>accounts.json</c>. Credentials are not here — see <see cref="Security.ICredentialStore"/>.
///
/// <para><b>A failed load never turns into a wipe.</b> When a stored list exists but can't
/// be decoded, the file is kept aside and saving an empty list is refused — the same failure
/// once wiped every account on macOS (<c>accountsLoadFailed</c>).</para>
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private bool _loadFailed;

    public AccountStore(string path)
    {
        _path = path;
    }

    public List<CloudAccount> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<CloudAccount>>(File.ReadAllText(_path), Options) ?? [];
            }
            catch (JsonException e)
            {
                _loadFailed = true;
                SupportLogger.Shared.Log($"accounts.json could not be decoded ({e.Message}) — it will not be overwritten", "Accounts", LogLevel.Error);
                try
                {
                    File.Copy(_path, $"{_path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json", overwrite: true);
                }
                catch (Exception copyFailure) when (copyFailure is IOException or UnauthorizedAccessException)
                {
                }

                return [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable right now: it may well hold accounts, so it must not
                // be replaced by whatever this session saves.
                _loadFailed = true;
                SupportLogger.Shared.Log($"accounts.json could not be read ({e.Message}) — it will not be overwritten", "Accounts", LogLevel.Error);
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<CloudAccount> accounts)
    {
        lock (_gate)
        {
            if (_loadFailed && accounts.Count == 0)
            {
                SupportLogger.Shared.Log("Save skipped: stored accounts couldn't be decoded, refusing to overwrite them", "Accounts", LogLevel.Error);
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(accounts, Options));
                File.Move(temporary, _path, overwrite: true);
                _loadFailed = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SupportLogger.Shared.Log($"Saving accounts failed: {e.Message}", "Accounts", LogLevel.Error);
            }
        }
    }
}
