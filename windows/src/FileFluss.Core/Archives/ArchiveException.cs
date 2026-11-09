// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Archives;

public enum ArchiveErrorKind
{
    /// <summary>The file couldn't be read as an archive. Carries the reader's own message.</summary>
    Unreadable,

    /// <summary>The entry list was readable but the data is encrypted; there is no password prompt yet.</summary>
    Encrypted,

    /// <summary>A helper tool (rar) exited non-zero.</summary>
    ToolFailed,

    ToolMissing,
    AlreadyExists,

    /// <summary>An entry's path would have written outside the destination.</summary>
    UnsafeEntry,

    NotInArchive,

    /// <summary>Fetching RARLAB's rar tool failed; the message says why.</summary>
    ToolInstall,

    Failed,
}

/// <summary>An archive operation that failed for a reason the user can be told (macOS <c>ArchiveError</c>).</summary>
public sealed class ArchiveException : Exception
{
    private ArchiveException(ArchiveErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public ArchiveErrorKind Kind { get; }

    public static ArchiveException Unreadable(string detail, Exception? inner = null)
        => new(ArchiveErrorKind.Unreadable, Localization.L("The archive could not be read: {0}", detail), inner);

    public static ArchiveException Encrypted()
        => new(ArchiveErrorKind.Encrypted, Localization.L("This archive is password-protected, which FileFluss cannot open yet."));

    public static ArchiveException ToolFailed(string tool, int status, string output)
    {
        var detail = output.Trim();
        return new(ArchiveErrorKind.ToolFailed, detail.Length == 0
            ? Localization.L("{0} failed with status {1}.", tool, status)
            : Localization.L("{0} failed: {1}", tool, detail));
    }

    public static ArchiveException ToolMissing(string tool)
        => new(ArchiveErrorKind.ToolMissing, Localization.L("{0} is missing from this system.", tool));

    public static ArchiveException AlreadyExists(string name)
        => new(ArchiveErrorKind.AlreadyExists, Localization.L("\"{0}\" already exists.", name));

    public static ArchiveException UnsafeEntry(string name)
        => new(ArchiveErrorKind.UnsafeEntry, Localization.L("The archive contains an unsafe entry name (\"{0}\"), so it was skipped.", name));

    public static ArchiveException NotInArchive(string name)
        => new(ArchiveErrorKind.NotInArchive, Localization.L("\"{0}\" is not in the archive.", name));

    public static ArchiveException ToolInstall(string message) => new(ArchiveErrorKind.ToolInstall, message);

    /// <summary>A job that failed for a reason already put into words.</summary>
    public static ArchiveException Failed(string message) => new(ArchiveErrorKind.Failed, message);
}
