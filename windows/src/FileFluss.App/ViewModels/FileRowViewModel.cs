// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows.Media;
using FileFluss.App.Mvvm;
using FileFluss.App.Services;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.ViewModels;

/// <summary>One row of a file list: an item plus everything the columns display.</summary>
public sealed class FileRowViewModel : Observable
{
    private readonly bool _isLocal;
    private readonly bool _hideExtension;
    private ImageSource? _icon;
    private ImageSource? _thumbnail;
    private bool _iconLoaded;
    private string? _kind;
    private bool _isRenaming;
    private string _editName = string.Empty;
    private bool _isCut;
    private bool _isDropTarget;
    private long? _folderSize;

    public FileRowViewModel(CloudFileItem item, bool isLocal, bool hideExtension)
    {
        Item = item;
        _isLocal = isLocal;
        _hideExtension = hideExtension;
    }

    public CloudFileItem Item { get; }

    public string Name => Item.Name;

    public string Path => Item.Path;

    public bool IsDirectory => Item.IsDirectory;

    public bool IsDrive => Item.IconOverride?.StartsWith("drive", StringComparison.Ordinal) == true;

    public string DisplayName
        => _hideExtension && !Item.IsDirectory && System.IO.Path.HasExtension(Item.Name) && !Item.Name.StartsWith('.')
            ? System.IO.Path.GetFileNameWithoutExtension(Item.Name)
            : Item.Name;

    /// <summary>Hidden files are shown dimmed, as Explorer does, when "show hidden" is on.</summary>
    public double Opacity => IsCut || Item.IsHidden ? 0.55 : 1.0;

    public bool IsCut
    {
        get => _isCut;
        set
        {
            if (Set(ref _isCut, value))
            {
                OnPropertyChanged(nameof(Opacity));
            }
        }
    }

    public bool IsEvicted => Item.DownloadStatus == CloudDownloadStatus.Evicted;

    /// <summary>
    /// The type icon, or — for local images and PDFs, as on macOS — a content thumbnail once
    /// it has been made. Read only while the row is on screen (the list virtualizes), so only
    /// visible rows ask for thumbnails; one that scrolled away before its turn asks again
    /// when it comes back.
    /// </summary>
    public ImageSource? Icon
    {
        get
        {
            if (_thumbnail is not null)
            {
                return _thumbnail;
            }

            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = IsDrive ? IconCache.ForPath(Item.Path, true) : IconCache.For(Item, _isLocal);
            }

            if (ThumbnailCache.ShouldUseThumbnail(Item, _isLocal) && !IsDrive)
            {
                _thumbnail = ThumbnailCache.Get(Item, OnThumbnailLoaded);
                if (_thumbnail is not null)
                {
                    return _thumbnail;
                }
            }

            return _icon;
        }
    }

    private void OnThumbnailLoaded(ImageSource thumbnail)
    {
        if (_thumbnail is null)
        {
            _thumbnail = thumbnail;
            OnPropertyChanged(nameof(Icon));
        }
    }

    public string SizeText
    {
        get
        {
            if (IsDrive)
            {
                return Item.Size > 0 ? ByteFormatter.Format(Item.Size) : string.Empty;
            }

            if (Item.IsDirectory)
            {
                return _folderSize is { } size ? ByteFormatter.Format(size) : "--";
            }

            return ByteFormatter.Format(Item.Size);
        }
    }

    /// <summary>Filled in by "Calculate Folder Size" so the Size column can show it.</summary>
    public long? FolderSize
    {
        get => _folderSize;
        set
        {
            if (Set(ref _folderSize, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public long SortSize => Item.IsDirectory ? _folderSize ?? -1 : Item.Size;

    public string DateText => Item.HasKnownModificationDate ? Item.ModificationDate.ToString("g", Culture) : "--";

    public string CreatedText => Item.CreationDate > CloudFileItem.UnknownDate ? Item.CreationDate.ToString("g", Culture) : "--";

    public string KindText => _kind ??= ComputeKind();

    private string ComputeKind()
    {
        if (IsDrive)
        {
            return L("Drive");
        }

        if (Item.Role == CloudItemRole.VirtualContainer)
        {
            return L("Folder");
        }

        return ShellIcons.TypeName(Item.Name, Item.IsDirectory);
    }

    public string ToolTip => Item.IsDirectory ? Item.Name : $"{Item.Name}\n{SizeText} · {DateText}";

    // MARK: - Inline rename

    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value);
    }

    // MARK: - Drag & drop

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public bool AcceptsDrops => Item.IsDirectory && Item.Role != CloudItemRole.VirtualContainer;

    public override string ToString() => Name;

    /// <summary>Dates and sizes are formatted in the chosen language; re-read them.</summary>
    public void OnLanguageChanged()
    {
        _kind = null;
        OnAllPropertiesChanged();
    }

    public static string Extension(string name) => System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

    public bool IsImage => Item.Extension is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff" or "webp" or "heic" or "ico";
}
