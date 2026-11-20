// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

// Debug builds only, like the version test: never compiled into Release.
#if DEBUG

using System.IO;
using System.Text.Json;
using System.Windows;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Settings;
using FileFluss.Native;

namespace FileFluss.App.Services;

/// <summary>
/// Help → "Save Test Accounts…" / "Restore Test Accounts…" (macOS 1.6, debug only): the
/// connected test accounts and their credential files in one file, outside the repository and
/// outside the debug data folder (so resetting that folder keeps them), readable by this
/// Windows user only. English-only, like the version test.
/// </summary>
public static class TestAccountsMenu
{
    private const string Title = "Test Accounts";

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileFluss Test Accounts", "test-accounts.json");

    private static string CredentialsFolder => Path.Combine(SettingsStore.DataDirectory, "credentials");

    public static void Save(Window owner)
    {
        var accounts = AppServices.Current.Accounts.Accounts.ToList();
        if (accounts.Count == 0)
        {
            MessageDialog.Info(owner, "Nothing to save", "There are no cloud accounts in this build.");
            return;
        }

        if (Load() is { } existing && existing.Accounts.Count > accounts.Count)
        {
            var missing = existing.Missing(accounts.Select(a => a.Id)).Count;
            if (!MessageDialog.Confirm(owner, "Replace the saved test accounts?",
                    $"The saved set has {existing.Accounts.Count} accounts, this build has {accounts.Count}. The {missing} not here any more would be dropped from it.",
                    "Replace", destructive: true))
            {
                return;
            }
        }

        try
        {
            var snapshot = Write(TestAccountSnapshot.Merge(accounts, CurrentCredentials(), null, DateTimeOffset.Now));
            MessageDialog.Info(owner, "Test accounts saved", $"{snapshot.Accounts.Count} accounts and {snapshot.Credentials.Count} credential files saved to {FilePath}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageDialog.Error(owner, "Couldn't save the test accounts", e.Message);
        }
    }

    public static async void Restore(Window owner)
    {
        if (Load() is not { } snapshot)
        {
            MessageDialog.Info(owner, "No saved test accounts", $"Save them first with Help → Save Test Accounts. Looked in {FilePath}.");
            return;
        }

        var services = AppServices.Current;
        var missing = snapshot.Missing(services.Accounts.Accounts.Select(a => a.Id));
        if (missing.Count == 0)
        {
            MessageDialog.Info(owner, "Nothing to restore", $"All {snapshot.Accounts.Count} saved accounts are already here.");
            return;
        }

        var names = string.Join(Environment.NewLine, missing.Select(a => a.DisplayName).Order(StringComparer.CurrentCultureIgnoreCase));
        if (!MessageDialog.Confirm(owner, $"Restore {missing.Count} test accounts?",
                $"Saved {snapshot.SavedAt.LocalDateTime:g}. Accounts already here stay as they are.{Environment.NewLine}{Environment.NewLine}{names}",
                "Restore", icon: DialogIcon.Question))
        {
            return;
        }

        // Credential files already here are newer than the saved ones: only the absent ones go back.
        var failed = new List<string>();
        Directory.CreateDirectory(CredentialsFolder);
        PrivateFolder.Apply(CredentialsFolder);
        foreach (var (name, data) in snapshot.Credentials)
        {
            var target = Path.Combine(CredentialsFolder, Path.GetFileName(name));
            try
            {
                if (!File.Exists(target))
                {
                    File.WriteAllBytes(target, data);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(name);
            }
        }

        var added = await services.Accounts.RestoreTestAccountsAsync(missing);
        SupportLogger.Shared.Log($"Restored {added} test account(s) from {FilePath}", VersionTestEngine.LogCategory, LogLevel.Notice);
        if (failed.Count > 0)
        {
            MessageDialog.Error(owner, "Some credentials couldn't be written", string.Join(Environment.NewLine, failed));
        }
    }

    /// <summary>
    /// After a version test: tokens some services rotate on every refresh are stale in the file
    /// by now. Updates the accounts that are here and keeps the ones that aren't.
    /// </summary>
    public static void RefreshAfterVersionTest()
    {
        if (Load() is not { } existing)
        {
            return;
        }

        try
        {
            Write(TestAccountSnapshot.Merge(AppServices.Current.Accounts.Accounts.ToList(), CurrentCredentials(), existing, DateTimeOffset.Now));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            SupportLogger.Shared.Log($"Test account snapshot not refreshed: {e.Message}", VersionTestEngine.LogCategory, LogLevel.Error);
        }
    }

    private static TestAccountSnapshot? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<TestAccountSnapshot>(File.ReadAllText(FilePath), TestAccountSnapshot.Options) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static TestAccountSnapshot Write(TestAccountSnapshot snapshot)
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        PrivateFolder.Apply(folder);

        // One generation back, in case a save went wrong; the atomic write keeps it as .bak.
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, TestAccountSnapshot.Options));
        return snapshot;
    }

    /// <summary>The credential store's files as they are: still DPAPI-encrypted for this user.</summary>
    private static Dictionary<string, byte[]> CurrentCredentials()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(CredentialsFolder))
        {
            return files;
        }

        foreach (var file in Directory.EnumerateFiles(CredentialsFolder, "*.bin"))
        {
            try
            {
                files[Path.GetFileName(file)] = File.ReadAllBytes(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files;
    }
}

#endif
