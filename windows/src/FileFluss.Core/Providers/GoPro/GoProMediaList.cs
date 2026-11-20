// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.GoPro;

/// <summary>
/// The camera's <c>GET /gopro/media/list</c> (legacy: <c>/gp/gpMediaList</c>) response:
/// <code>
/// { "id": "…", "media": [ { "d": "100GOPRO",
///     "fs": [ { "n": "GH010397.MP4", "cre": "1696600109", "mod": "1696600109", "s": "11587660" } ] } ] }
/// </code>
/// Files are grouped by on-card directory (<c>d</c>). Numeric fields arrive as strings on
/// some firmware and as bare numbers on other, so every scalar is read leniently.
/// </summary>
internal sealed class GoProMediaList
{
    internal sealed record MediaFile(string Name, long Size, DateTimeOffset? Modified, DateTimeOffset? Created);

    internal sealed record MediaDirectory(string Name, IReadOnlyList<MediaFile> Files);

    private GoProMediaList(IReadOnlyList<MediaDirectory> media)
    {
        Media = media;
    }

    public IReadOnlyList<MediaDirectory> Media { get; }

    public static GoProMediaList Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("media", out var media) ||
                media.ValueKind != JsonValueKind.Array)
            {
                throw CloudProviderException.InvalidResponse();
            }

            var directories = new List<MediaDirectory>();
            foreach (var directory in media.EnumerateArray())
            {
                if (directory.ValueKind != JsonValueKind.Object ||
                    !directory.TryGetProperty("d", out var name) ||
                    name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var files = new List<MediaFile>();
                if (directory.TryGetProperty("fs", out var fs) && fs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in fs.EnumerateArray())
                    {
                        if (file.ValueKind != JsonValueKind.Object ||
                            !file.TryGetProperty("n", out var fileName) ||
                            fileName.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        files.Add(new MediaFile(fileName.GetString()!, Scalar(file, "s") ?? 0, Date(file, "mod"), Date(file, "cre")));
                    }
                }

                directories.Add(new MediaDirectory(name.GetString()!, files));
            }

            return new GoProMediaList(directories);
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    /// <summary>
    /// The listing for a path: <c>"/"</c> → one folder per on-card directory (sized as the sum
    /// of its files, dated by the newest), <c>"/100GOPRO"</c> → its media files. Paths are always
    /// <c>/&lt;dir&gt;</c> and <c>/&lt;dir&gt;/&lt;file&gt;</c> so they round-trip through download and delete.
    /// </summary>
    public IReadOnlyList<CloudFileItem> Items(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0)
        {
            return Media.Select(FolderItem).ToList();
        }

        var directory = Media.FirstOrDefault(d => d.Name == trimmed);
        return directory is null ? [] : directory.Files.Select(f => FileItem(directory.Name, f)).ToList();
    }

    /// <summary>A single file by its <c>/&lt;dir&gt;/&lt;file&gt;</c> path; null for folders and unknown paths.</summary>
    public CloudFileItem? File(string path)
    {
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 2)
        {
            return null;
        }

        var directory = Media.FirstOrDefault(d => d.Name == components[0]);
        var file = directory?.Files.FirstOrDefault(f => f.Name == components[1]);
        return file is null ? null : FileItem(directory!.Name, file);
    }

    /// <summary>An on-card directory by its <c>/&lt;dir&gt;</c> path; null otherwise.</summary>
    public CloudFileItem? Folder(string path)
    {
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 1)
        {
            return null;
        }

        var directory = Media.FirstOrDefault(d => d.Name == components[0]);
        return directory is null ? null : FolderItem(directory);
    }

    private static CloudFileItem FolderItem(MediaDirectory directory)
    {
        long total = 0;
        var newest = CloudFileItem.UnknownDate;
        foreach (var file in directory.Files)
        {
            total += file.Size;
            if (file.Modified is { } modified && modified > newest)
            {
                newest = modified;
            }
        }

        var path = "/" + directory.Name;
        return new CloudFileItem(path, directory.Name, path, true, total, newest);
    }

    private static CloudFileItem FileItem(string directory, MediaFile file)
    {
        var path = $"/{directory}/{file.Name}";
        return new CloudFileItem(path, file.Name, path, false, file.Size, file.Modified ?? file.Created ?? CloudFileItem.UnknownDate)
        {
            CreationDate = file.Created ?? CloudFileItem.UnknownDate,
        };
    }

    private static long? Scalar(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number => (long)value.GetDouble(),
            _ => null,
        };
    }

    private static DateTimeOffset? Date(JsonElement element, string name)
        => Scalar(element, name) is { } seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
