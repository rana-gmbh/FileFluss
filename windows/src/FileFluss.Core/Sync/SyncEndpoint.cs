// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Sync;

/// <summary>
/// One side of a sync or a comparison: a folder on a <see cref="FileProvider"/> — the local
/// disk or a cloud account. The macOS app had separate local/cloud cases; here every
/// operation goes through the provider, so one code path serves both.
/// </summary>
/// <param name="Path">The folder, in the provider's own path syntax.</param>
public sealed record SyncEndpoint(FileProvider Provider, string Path)
{
    /// <summary>The account's name for the endpoint card ("Dropbox (Robert)"); null for the local disk.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// A read-only snapshot (for instance a provider over the offline search index of a
    /// disconnected account). Compare reads it; a sync refuses to write to it.
    /// </summary>
    public bool IsOffline { get; init; }

    public bool IsCloud => !Provider.IsLocal;

    /// <summary>A real folder: "This PC" (the local provider's root) holds drives, not files.</summary>
    public bool IsUsable => !(Provider.IsLocal && Path.Length == 0);

    /// <summary>The path line of the endpoint card.</summary>
    public string DisplayPath
    {
        get
        {
            if (IsOffline)
            {
                var path = Provider.IsRoot(Path) ? string.Empty : Path;
                return Localization.L("{0} (offline)", DisplayName ?? string.Empty) + path;
            }

            return Provider.IsLocal || Path.Length > 0 ? Path : "/";
        }
    }

    /// <summary>Short name of the folder itself ("Photos"), or the account name at an account root.</summary>
    public string FolderName
        => Provider.IsRoot(Path) ? DisplayName ?? Provider.GetName(Path) : Provider.GetName(Path);

    /// <summary>
    /// The provider path of <paramref name="relativePath"/> ("a/b.txt") beneath this endpoint.
    /// The components are names another side listed — a hostile server's included — so a
    /// ".." is refused, and on the local disk a name that isn't one plain Windows component
    /// (<c>..\..\Startup\x.exe</c>, <c>C:\x</c>, <c>a:b</c>) is made safe the way the
    /// transfer engine names files; the result never leaves the endpoint's folder.
    /// </summary>
    /// <exception cref="CloudProviderException">A component would climb out of the endpoint.</exception>
    public string Resolve(string relativePath)
    {
        var path = Path;
        foreach (var component in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Provider.IsLocal)
            {
                path = Provider.CombinePath(path, SafeLocalPath.Component(component));
            }
            else if (CloudPath.IsTraversalName(component))
            {
                throw SafeLocalPath.UnsafeName(component);
            }
            else
            {
                path = Provider.CombinePath(path, component);
            }
        }

        if (Provider.IsLocal && Path.Length > 0)
        {
            SafeLocalPath.EnsureWithin(path, Path, relativePath);
        }

        return path;
    }

    /// <summary>
    /// The two endpoints are the same folder, or one lies inside the other, on the same
    /// provider. Syncing such a pair would copy a tree into itself.
    /// </summary>
    public bool Overlaps(SyncEndpoint other)
    {
        if (!ReferenceEquals(Provider, other.Provider))
        {
            return false;
        }

        if (Provider.IsLocal)
        {
            var a = Normalized(Path);
            var b = Normalized(other.Path);
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) || Inside(a, b) || Inside(b, a);
        }

        return CloudPath.IsWithin(Path, other.Path) || CloudPath.IsWithin(other.Path, Path);

        static bool Inside(string child, string parent)
        {
            // A drive root keeps its separator ("C:\"); anything else gets one appended.
            var prefix = System.IO.Path.EndsInDirectorySeparator(parent) ? parent : parent + System.IO.Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        static string Normalized(string path)
        {
            try
            {
                return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                return path;
            }
        }
    }
}
