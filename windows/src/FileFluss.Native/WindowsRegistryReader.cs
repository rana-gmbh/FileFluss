// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Providers.ICloud;
using Microsoft.Win32;

namespace FileFluss.Native;

/// <summary>Registry access for Core code that can't reference Microsoft.Win32 (the iCloud Drive locator).</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        using var key = Open(keyPath);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyDictionary<string, string> GetStringValues(string keyPath)
    {
        using var key = Open(keyPath);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (key is null)
        {
            return values;
        }

        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text)
            {
                values[name] = text;
            }
        }

        return values;
    }

    private static RegistryKey? Open(string keyPath)
    {
        var separator = keyPath.IndexOf('\\');
        var hive = separator < 0 ? keyPath : keyPath[..separator];
        var rest = separator < 0 ? string.Empty : keyPath[(separator + 1)..];
        var root = hive.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            _ => null,
        };
        try
        {
            return rest.Length == 0 ? root : root?.OpenSubKey(rest);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
