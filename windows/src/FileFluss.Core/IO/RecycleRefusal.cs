// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.IO;

/// <summary>Why Windows refused to move an item to the Recycle Bin (macOS <c>TrashRefusal</c>).</summary>
public enum RecycleRefusalKind
{
    /// <summary>The drive has no Recycle Bin (network share, most removable media).</summary>
    NoRecycleBin,

    /// <summary>Another program has the item open.</summary>
    InUse,

    /// <summary>
    /// This user may not change the item's folder, so it can neither be recycled nor deleted
    /// (macOS <c>folderNotWritable</c>).
    /// </summary>
    NoPermission,

    /// <summary>
    /// The item itself could be deleted, but the drive's Recycle Bin folder is damaged or shut
    /// to this user — Explorer runs into the same thing (macOS <c>trashFolderUnwritable</c>).
    /// </summary>
    RecycleBinDamaged,

    /// <summary>The path is longer than the Recycle Bin accepts (MAX_PATH).</summary>
    PathTooLong,

    /// <summary>The item is no longer there.</summary>
    NotFound,

    /// <summary>Refused for want of permission, with nothing visible to blame.</summary>
    AccessDenied,

    /// <summary>Any other failure; the code is shown so a report says what it was.</summary>
    Unknown,
}

/// <summary>
/// A refused move to the Recycle Bin and what the check found: the kind, the shell's error
/// code, and the place the explanation names (the item's folder, or the drive whose Recycle
/// Bin is at fault).
/// </summary>
public sealed record RecycleRefusal(RecycleRefusalKind Kind, int Code = 0, string? Location = null)
{
    /// <summary>
    /// The first guess from an <c>SHFileOperation</c> result, which mixes the shell's legacy
    /// <c>DE_*</c> codes with plain Win32 errors. The native side then looks closer at
    /// <see cref="RecycleRefusalKind.AccessDenied"/> and <see cref="RecycleRefusalKind.Unknown"/>.
    /// </summary>
    public static RecycleRefusalKind Classify(int code) => code switch
    {
        2 or 3 or 0x7C => RecycleRefusalKind.NotFound,                       // file / path not found, DE_INVALIDFILES
        5 or 0x78 or 0x10000 => RecycleRefusalKind.AccessDenied,             // ERROR_ACCESS_DENIED, DE_ACCESSDENIEDSRC, DE_ERROR_ONDEST
        32 or 33 => RecycleRefusalKind.InUse,                                // sharing / lock violation
        206 or 0x79 or 0x81 => RecycleRefusalKind.PathTooLong,               // ERROR_FILENAME_EXCED_RANGE, DE_PATHTOODEEP, DE_FILENAMETOOLONG
        1392 or 1393 or unchecked((int)0x80070570) => RecycleRefusalKind.RecycleBinDamaged, // ERROR_FILE_CORRUPT, ERROR_DISK_CORRUPT
        _ => RecycleRefusalKind.Unknown,
    };

    /// <summary>
    /// Whether deleting outright is worth offering. Not when the folder can't be changed or the
    /// file is open: deleting needs the same access and would only fail again.
    /// </summary>
    public bool OffersPermanentDelete => Kind is RecycleRefusalKind.RecycleBinDamaged or RecycleRefusalKind.PathTooLong
        or RecycleRefusalKind.AccessDenied or RecycleRefusalKind.Unknown or RecycleRefusalKind.NoRecycleBin;

    /// <summary>The dialog title for this refusal.</summary>
    public string Title => Kind == RecycleRefusalKind.RecycleBinDamaged
        ? Localization.L("The Recycle Bin can't be used")
        : Localization.L("FileFluss couldn't move this to the Recycle Bin");

    /// <summary>The explanation; <paramref name="label"/> is <c>"name"</c> or "3 items".</summary>
    public string Message(string label) => Kind switch
    {
        RecycleRefusalKind.NoRecycleBin
            => Localization.L("{0} can't be moved to the Recycle Bin because its drive doesn't have one. Delete permanently? This can't be undone.", label),
        RecycleRefusalKind.InUse
            => Localization.L("Windows refused to move {0} to the Recycle Bin because it is open in another program. Close it there and try again.", label),
        RecycleRefusalKind.NoPermission
            => Localization.L("You don't have permission to change the folder {0} is in ({1}), so it can't be moved to the Recycle Bin or deleted. Its owner can change this in File Explorer under Properties → Security.", label, Location ?? string.Empty),
        RecycleRefusalKind.RecycleBinDamaged
            => Localization.L("Windows refused to move {0} to the Recycle Bin because the Recycle Bin on {1} is damaged or can't be written to. File Explorer runs into the same problem. Emptying the Recycle Bin of that drive usually repairs it: in Settings → System → Storage → Temporary files, or by right-clicking the Recycle Bin on the desktop. Deleting permanently can't be undone.", label, Location ?? string.Empty),
        RecycleRefusalKind.PathTooLong
            => Localization.L("{0} can't be moved to the Recycle Bin because its path is longer than the Recycle Bin accepts. Delete permanently? This can't be undone.", label),
        RecycleRefusalKind.NotFound
            => Localization.L("{0} is no longer there. It may have been moved or deleted outside FileFluss.", label),
        _ => Localization.L("Windows refused to move {0} to the Recycle Bin (error {1}). If File Explorer can't move it to the Recycle Bin either, the cause lies outside FileFluss. Deleting permanently can't be undone.", label, CodeText),
    };

    /// <summary>The code as the support log and the message show it.</summary>
    public string CodeText => $"0x{Code:X}";
}

/// <summary>
/// Thrown by <see cref="Providers.LocalFileProvider.DeleteItemAsync"/> when the Recycle Bin
/// refused an item. An <see cref="IOException"/>, so every caller that handles file-system
/// failures handles it too; the panel reads <see cref="Refusal"/> to explain and offer a way out.
/// </summary>
public sealed class RecycleRefusedException : IOException
{
    public RecycleRefusedException(string path, RecycleRefusal refusal)
        : base(refusal.Message($"\"{DisplayName(path)}\""))
    {
        ItemPath = path;
        Refusal = refusal;
    }

    public string ItemPath { get; }

    public RecycleRefusal Refusal { get; }

    private static string DisplayName(string path)
        => Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
}
