// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Providers.GitHub;

internal enum GitHubPathKind
{
    /// <summary><c>/</c> — the owners the token can see.</summary>
    Root,

    /// <summary><c>/rana-gmbh</c> — that owner's repositories.</summary>
    Owner,

    /// <summary><c>/rana-gmbh/FileFluss</c> — one repository: its Source folder and its archive.</summary>
    Repository,

    /// <summary><c>/rana-gmbh/FileFluss/FileFluss-main.zip</c> — the synthetic snapshot of the default branch.</summary>
    Archive,

    /// <summary><c>/rana-gmbh/FileFluss/Source/…</c> — the repository's files. An empty <see cref="GitHubPath.TreePath"/> is the Source folder itself.</summary>
    Tree,
}

/// <summary>
/// Where a path inside a GitHub account points. The account presents a three-level
/// hierarchy — owner, repository, then either the repository's files or a snapshot archive:
/// <code>
/// /                                       owners the token can see
/// /rana-gmbh                              that owner's repositories
/// /rana-gmbh/FileFluss                    one repository
/// /rana-gmbh/FileFluss/Source             its files, browsable
/// /rana-gmbh/FileFluss/FileFluss-main.zip a snapshot of the default branch
/// </code>
/// <para><c>Source</c> is a fixed, unlocalized path segment on purpose: these paths are
/// persisted in favourites, the offline index and sync rules, so they have to mean the same
/// thing in every language and every future release.</para>
/// </summary>
internal sealed record GitHubPath(GitHubPathKind Kind, string Owner = "", string Repo = "", string TreePath = "", string Filename = "")
{
    public const string SourceFolderName = "Source";

    public static readonly GitHubPath Root = new(GitHubPathKind.Root);

    /// <summary>
    /// Parses an account-relative path. Null for anything that can't name something in a
    /// GitHub account, which callers report as "not found" rather than guessing.
    /// </summary>
    public static GitHubPath? Parse(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 0:
                return Root;
            case 1:
                return new GitHubPath(GitHubPathKind.Owner, parts[0]);
            case 2:
                return new GitHubPath(GitHubPathKind.Repository, parts[0], parts[1]);
            default:
                if (parts[2] == SourceFolderName)
                {
                    return new GitHubPath(GitHubPathKind.Tree, parts[0], parts[1], TreePath: string.Join('/', parts.Skip(3)));
                }

                // A zip is the archive only directly inside the repository; one committed to
                // the repository lives under Source and stays a tree entry.
                if (parts.Length == 3 && parts[2].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return new GitHubPath(GitHubPathKind.Archive, parts[0], parts[1], Filename: parts[2]);
                }

                return null;
        }
    }

    /// <summary>
    /// The archive filename for a repository. A branch name may contain slashes
    /// ("release/1.5"), which would otherwise read as extra path components, so they are
    /// flattened.
    /// </summary>
    public static string ArchiveFilename(string repo, string branch) => $"{repo}-{branch.Replace('/', '-')}.zip";
}
