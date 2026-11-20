// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.Cryptography;
using System.Text;

namespace FileFluss.Core.Mount;

/// <summary>
/// A small on-disk store keyed by provider paths (macOS <c>WebDAVSidecarStore</c>). The
/// <see cref="WebDavServer"/> uses it to absorb the metadata files a file manager writes
/// while browsing — Explorer's <c>desktop.ini</c> and <c>Thumbs.db</c>, Finder's
/// <c>.DS_Store</c> and <c>._*</c> forks — without pushing them to the user's cloud, while
/// still serving them back so the folder's view state survives a revisit.
///
/// <para>macOS mirrored the path tree below the root. Here each entry is a flat file named
/// by the SHA-256 of its path: cloud folder names may contain characters NTFS rejects
/// (<c>: ? * " &lt; &gt; |</c>), trailing dots or reserved names like <c>CON</c>, and a hash
/// sidesteps all of that as well as any <c>..</c> trickery in a request path.</para>
/// </summary>
public sealed class WebDavSidecarStore
{
    public WebDavSidecarStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    /// <summary>Stores a copy of <paramref name="sourceFile"/> (an empty file when null) under <paramref name="path"/>.</summary>
    public void Put(string path, string? sourceFile)
    {
        Directory.CreateDirectory(Root);
        var target = FilePath(path);
        var staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
        if (sourceFile is null)
        {
            File.WriteAllBytes(staging, []);
        }
        else
        {
            File.Copy(sourceFile, staging, overwrite: true);
        }

        File.Move(staging, target, overwrite: true);
    }

    /// <summary>Opens the stored file for reading, or null when there is none.</summary>
    public FileStream? Open(string path)
    {
        try
        {
            return WebDavReadCache.OpenShared(FilePath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(string path)
    {
        try
        {
            File.Delete(FilePath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Size and modification time of the stored file, or null when there is none.</summary>
    public (long Size, DateTimeOffset Modified)? Metadata(string path)
    {
        var info = new FileInfo(FilePath(path));
        return info.Exists ? (info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null;
    }

    public bool Exists(string path) => File.Exists(FilePath(path));

    /// <summary>Best-effort wipe of the whole store, for a mount that is going away for good.</summary>
    public void Clear()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        Directory.CreateDirectory(Root);
    }

    private string FilePath(string path)
        => Path.Combine(Root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path))));
}
