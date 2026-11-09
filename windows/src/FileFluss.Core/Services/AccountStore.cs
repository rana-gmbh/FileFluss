// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Services;

/// <summary>
/// The list of connected accounts (macOS <c>UserDefaults["cloudAccounts"]</c>), as JSON in
/// <c>accounts.json</c>. Credentials are not here — see <see cref="Security.ICredentialStore"/>.
///
/// <para><b>A failed load never turns into a wipe.</b> When a stored list exists but can't
/// be decoded, the file is kept aside and saving an empty list is refused — the same failure
/// once wiped every account on macOS (<c>accountsLoadFailed</c>). Writes are durable and keep
/// the previous list as <c>accounts.json.bak</c> (<see cref="AtomicFile"/>), which a load falls
/// back to when the main file is damaged. When the file could not even be read (locked by a
/// scanner at launch), the accounts in it are merged back into the next save instead of being
/// overwritten by the shorter list the app saw.</para>
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private bool _loadFailed;
    private bool _unread;

    public AccountStore(string path)
    {
        _path = path;
    }

    public List<CloudAccount> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path) && !File.Exists(AtomicFile.BackupPath(_path)))
            {
                return [];
            }

            var text = ReadWithRetries(_path);
            if (text is null && File.Exists(_path))
            {
                // Present but unreadable (locked): remember to merge its accounts back later.
                _unread = true;
                _loadFailed = true;
                SupportLogger.Shared.Log("accounts.json could not be read — it will not be overwritten", "Accounts", LogLevel.Error);
            }
            else if (text is not null)
            {
                try
                {
                    return Decode(text);
                }
                catch (JsonException e)
                {
                    _loadFailed = true;
                    SupportLogger.Shared.Log($"accounts.json could not be decoded ({e.Message}) — it will not be overwritten", "Accounts", LogLevel.Error);
                    PreserveUnreadable();
                }
            }

            // The previous good list, if the last write left one.
            if (ReadWithRetries(AtomicFile.BackupPath(_path)) is { } backup)
            {
                try
                {
                    var restored = Decode(backup);
                    SupportLogger.Shared.Log($"Restored {restored.Count} account(s) from accounts.json.bak", "Accounts", LogLevel.Notice);
                    return restored;
                }
                catch (JsonException)
                {
                }
            }

            return [];
        }
    }

    public void Save(IReadOnlyList<CloudAccount> accounts)
    {
        lock (_gate)
        {
            var toWrite = accounts;
            if (_unread)
            {
                // The launch couldn't read the file, so the app doesn't know what is in it.
                // Read it now and keep every account it holds; if it still can't be read, don't
                // replace it with a list that may be missing accounts.
                if (ReadWithRetries(_path) is not { } text)
                {
                    SupportLogger.Shared.Log("Save skipped: accounts.json still can't be read, refusing to overwrite it", "Accounts", LogLevel.Error);
                    return;
                }

                try
                {
                    var known = accounts.Select(a => a.Id).ToHashSet();
                    toWrite = [.. accounts, .. Decode(text).Where(a => !known.Contains(a.Id))];
                }
                catch (JsonException)
                {
                    PreserveUnreadable();
                }

                _unread = false;
            }

            if (_loadFailed && toWrite.Count == 0)
            {
                SupportLogger.Shared.Log("Save skipped: stored accounts couldn't be decoded, refusing to overwrite them", "Accounts", LogLevel.Error);
                return;
            }

            try
            {
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(toWrite, Options));
                _loadFailed = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SupportLogger.Shared.Log($"Saving accounts failed: {e.Message}", "Accounts", LogLevel.Error);
            }
        }
    }

    private static List<CloudAccount> Decode(string text)
        => JsonSerializer.Deserialize<List<CloudAccount>>(text, Options) ?? throw new JsonException("The account list is null.");

    /// <summary>The file's text, retrying briefly through a sharing violation; null when it can't be read.</summary>
    private static string? ReadWithRetries(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                // An empty file or one of NULs (what a power cut leaves of an unflushed write)
                // fails to decode like any damaged file, and the backup is used.
                return File.ReadAllText(path);
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private void PreserveUnreadable()
    {
        try
        {
            File.Copy(_path, $"{_path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
