// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Search;

/// <summary>
/// What the offline view's banner and empty state say about a source (macOS
/// <c>OfflineSourceView</c>): its name, when it was indexed, and the fixed texts.
/// </summary>
public sealed record OfflineSourceInfo(string SourceId, string DisplayName, DateTimeOffset? LastIndexed)
{
    /// <summary>"Offline view — Dropbox (Robert)".</summary>
    public string Title => Localization.L("Offline view — {0}", DisplayName);

    /// <summary>"Indexed 3 days ago", "Never indexed".</summary>
    public string LastIndexedText => IndexingText.LastIndexed(LastIndexed, DateTimeOffset.Now);

    public static string ReadOnlyBadge => Localization.L("Read only");

    public static string EmptyTitle => Localization.L("No indexed contents");

    public static string EmptyMessage => Localization.L("Re-index this source while it's online to populate offline contents.");
}

/// <summary>
/// An offline account or an unplugged indexed drive, browsed from the search index: a
/// read-only <see cref="FileProvider"/>, so the ordinary panel shows it unchanged — folder by
/// folder, sortable, searchable — while every change is refused with a message saying why.
/// Port of the data side of macOS <c>OfflineSourceView</c>, which was a separate read-only
/// browser.
///
/// <para>Paths are POSIX: an account's own paths, or a drive's paths relative to its root
/// ("/Photos/2026"), as the index stores them.</para>
/// </summary>
public sealed class OfflineIndexProvider : FileProvider
{
    private readonly SearchIndex _index;
    private readonly string _rootPath;

    private OfflineIndexProvider(SearchIndex index, OfflineSourceKind kind, string sourceId, Guid? accountId, string displayName, string rootPath)
    {
        _index = index;
        SourceKind = kind;
        SourceId = sourceId;
        AccountId = accountId;
        DisplayName = displayName;
        _rootPath = CloudPath.Normalize(rootPath);
    }

    /// <summary>An account in offline mode or not connected: its cached tree from <c>cloud_files</c>.</summary>
    public static OfflineIndexProvider ForCloudAccount(SearchIndex index, CloudAccount account)
        => ForCloudAccount(index, account.Id, account.DisplayName, account.EffectiveRootPath);

    public static OfflineIndexProvider ForCloudAccount(SearchIndex index, Guid accountId, string displayName, string rootPath = "/")
        => new(index, OfflineSourceKind.Cloud, SearchIndex.SourceIdFor(accountId), accountId, displayName, rootPath);

    /// <summary>An unplugged drive: its last index from <c>indexed_files</c>.</summary>
    public static OfflineIndexProvider ForDrive(SearchIndex index, KnownDrive drive)
        => ForDrive(index, drive.Id, drive.DisplayName);

    public static OfflineIndexProvider ForDrive(SearchIndex index, string driveId, string displayName)
        => new(index, OfflineSourceKind.Drive, driveId, null, displayName, "/");

    public OfflineSourceKind SourceKind { get; }

    /// <summary>The account's id string or the drive's "vol:…" id.</summary>
    public string SourceId { get; }

    public Guid? AccountId { get; }

    public string DisplayName { get; }

    /// <summary>Always true: nothing here can be changed, opened or copied out.</summary>
    public bool IsReadOnly => true;

    public override string RootPath => _rootPath;

    /// <summary>The banner's facts. The date is that of the last full index; a source only ever browsed reads "Never indexed".</summary>
    public OfflineSourceInfo GetInfo()
    {
        var source = _index.GetSource(SourceId);
        var name = !string.IsNullOrWhiteSpace(DisplayName)
            ? DisplayName
            : source?.DisplayName is { Length: > 0 } stored ? stored : Localization.L("Offline Source");
        return new OfflineSourceInfo(SourceId, name, source?.LastIndexed);
    }

    // MARK: - Reading

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CloudFileItem>>(() =>
        {
            var folder = CloudPath.Normalize(path);
            var rows = SourceKind == OfflineSourceKind.Cloud
                ? _index.ListCloudChildren(AccountId!.Value, folder)
                : _index.ListChildren(SourceId, folder);
            cancellationToken.ThrowIfCancellationRequested();

            // An empty answer is fine for the root (nothing indexed yet: the view says so) and
            // for an indexed empty folder; for a folder the index has never heard of, the
            // panel should hear "not found", as from any other provider.
            if (rows.Count == 0 && folder != "/" && folder != _rootPath && Entry(folder) is not { IsDirectory: true })
            {
                throw CloudProviderException.NotFound(folder);
            }

            return rows.Select(r => r.ToCloudFileItem()).ToList();
        }, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var normalized = CloudPath.Normalize(path);
            if (normalized == "/" || normalized == _rootPath)
            {
                return new CloudFileItem(normalized, normalized == "/" ? DisplayName : CloudPath.Name(normalized), normalized, true, 0, CloudFileItem.UnknownDate);
            }

            return Entry(normalized)?.ToCloudFileItem() ?? throw CloudProviderException.NotFound(normalized);
        }, cancellationToken);

    /// <summary>Total bytes beneath a folder, summed by the index rather than by walking it.</summary>
    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => SourceKind == OfflineSourceKind.Cloud
            ? _index.CloudSizeUnder(AccountId!.Value, path)
            : _index.IndexedSizeUnder(SourceId, CloudPath.Normalize(path)), cancellationToken);

    /// <summary>Name search over this source's index, limited to <paramref name="path"/> when given.</summary>
    public override Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CloudFileItem>?>(() =>
        {
            var items = SourceKind == OfflineSourceKind.Cloud
                ? _index.Search(query, AccountId).Select(hit => hit.Item)
                : _index.SearchIndexed(query, [SourceId]).Select(row => row.ToCloudFileItem());
            if (!string.IsNullOrEmpty(path) && !CloudPath.IsRoot(path))
            {
                items = items.Where(item => CloudPath.IsWithin(item.Path, path) && CloudPath.Normalize(item.Path) != CloudPath.Normalize(path));
            }

            return items.ToList();
        }, cancellationToken);

    private IndexedFile? Entry(string path)
        => SourceKind == OfflineSourceKind.Cloud
            ? _index.CloudEntry(AccountId!.Value, path)
            : _index.IndexedEntry(SourceId, path);

    // MARK: - Refused: the source is offline

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    /// <summary>The message every refused operation carries.</summary>
    public CloudProviderException ReadOnly()
        => CloudProviderException.CommandFailed(Localization.L(
            "\"{0}\" is offline. Its indexed contents can be browsed and searched, but not opened, copied or changed.",
            DisplayName));
}
