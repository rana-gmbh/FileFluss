// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using FileFluss.Core.Providers;

namespace FileFluss.Core.IO;

/// <summary>
/// Builds local paths from names that came off the network.
///
/// <para>A remote name is attacker-controlled: a hostile server (self-hosted WebDAV,
/// Nextcloud and SFTP are first-class here) or merely someone who shares a file chooses it.
/// <see cref="Path.Combine(string, string)"/> happily accepts <c>..\..\</c>, so a name like
/// <c>..\..\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\evil.lnk</c> writes
/// far outside the folder the user picked. Two independent defences, as on macOS: the name is
/// reduced to one harmless component, and the result is checked to be inside the
/// directory.</para>
///
/// <para>Windows adds its own rules on top: <c>&lt;&gt;:"|?*</c> and control characters are
/// illegal, trailing dots and spaces are silently stripped (so "a." and "a" collide), and
/// device names such as CON or COM1 — with any extension — open the device instead of a
/// file. Names legal on a Mac or a Linux server routinely break each of these.</para>
/// </summary>
public static class SafeLocalPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>A remote name reduced to one safe Windows path component. Never empty, never "." or "..".</summary>
    public static string FileName(string remoteName)
    {
        var builder = new StringBuilder(remoteName.Length);
        foreach (var c in remoteName)
        {
            builder.Append(c switch
            {
                '/' or '\\' => '-',
                '<' or '>' or ':' or '"' or '|' or '?' or '*' => '_',
                < ' ' => '_',
                _ => c,
            });
        }

        // Trailing dots and spaces vanish when Windows creates the file; leading spaces are
        // legal but are almost always an accident in a synced name.
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.'))
        {
            cleaned = "unnamed";
        }

        var stem = cleaned.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        // NTFS allows 255 UTF-16 units per component; leave room for a "(2)" suffix or an
        // extension a provider appends during export.
        if (cleaned.Length > 200)
        {
            var extension = Path.GetExtension(cleaned);
            cleaned = extension.Length is > 0 and <= 16
                ? cleaned[..(200 - extension.Length)] + extension
                : cleaned[..200];
        }

        return cleaned;
    }

    /// <summary>Destination for <paramref name="remoteName"/> inside <paramref name="directory"/>, guaranteed to stay inside it.</summary>
    public static string Destination(string remoteName, string directory)
    {
        var candidate = Path.GetFullPath(Path.Combine(directory, FileName(remoteName)));
        if (!IsStrictlyInside(candidate, directory))
        {
            throw UnsafeName(remoteName);
        }

        return candidate;
    }

    /// <summary>
    /// One component of a relative path that came off the network (a sync or compare key),
    /// for a path below a local folder. A name Windows takes as one ordinary component is
    /// kept verbatim — so a local tree resolves to its own files — anything else
    /// (<c>a\..\..\x</c>, <c>C:\x</c>, <c>a:b</c> streams, <c>?</c>) is reduced by
    /// <see cref="FileName"/>, as the transfer engine names files. "." and ".." are refused.
    /// </summary>
    public static string Component(string remoteName)
    {
        if (CloudPath.IsTraversalName(remoteName))
        {
            throw UnsafeName(remoteName);
        }

        var plain = remoteName.Length > 0
                    && !remoteName.All(c => c == '.')
                    && !remoteName.Any(c => c is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*' or < ' ')
                    && !ReservedNames.Contains(remoteName.Split('.')[0].TrimEnd(' '));
        return plain ? remoteName : FileName(remoteName);
    }

    /// <summary>
    /// Throws unless <paramref name="path"/> is <paramref name="root"/> or lies below it once
    /// both are normalised — the last line of defence after names were made safe.
    /// </summary>
    public static void EnsureWithin(string path, string root, string name)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) && !IsStrictlyInside(full, fullRoot))
        {
            throw UnsafeName(name);
        }
    }

    private static bool IsStrictlyInside(string candidate, string directory)
    {
        var root = Path.GetFullPath(directory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    internal static CloudProviderException UnsafeName(string remoteName)
        => CloudProviderException.CommandFailed(
            Localization.L("The server returned an unsafe file name (\"{0}\"), so the download was stopped.", remoteName));
}
