// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Xml;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.Jottacloud;

/// <summary>
/// Parses the Jottacloud "JFS" XML payloads FileFluss needs: a single-level folder listing
/// (<c>&lt;folder&gt;&lt;folders&gt;…&lt;/folders&gt;&lt;files&gt;…&lt;/files&gt;&lt;/folder&gt;</c>), a folder's
/// own modified date, the account root (<c>&lt;user&gt;&lt;capacity/&gt;&lt;usage/&gt;…</c>) for quota,
/// and the public-share / error fields.
///
/// <para>The JFS API is undocumented; element and attribute names match the rclone backend's
/// observed shapes. Like the macOS SAX delegates this reads forward-only and keeps whatever
/// it parsed before a malformed tail, so a truncated body shows a partial folder rather than
/// an error.</para>
/// </summary>
internal static class JottacloudXmlParser
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
    };

    public sealed record AccountUsage(long Capacity, long Usage);

    public sealed record PublicShare(string PublicUri, string PublicSharePath);

    /// <summary>Child folders and completed, non-trashed files of one listing.</summary>
    public static IReadOnlyList<CloudFileItem> ParseListing(string xml, string basePath)
    {
        var items = new List<CloudFileItem>();
        var inFolders = false;
        var inFiles = false;

        // The file being built. A file carries a <currentRevision> (the finished upload) and,
        // while a new upload is pending, a <latestRevision>. The macOS delegate let whichever
        // came last win, which hid a file for as long as a re-upload was in flight; rclone
        // reads only currentRevision. Here currentRevision wins and latestRevision is the
        // fallback for files that have nothing else (whose state then decides, as on macOS).
        string? fileName = null;
        var fileDeleted = false;
        var current = new Revision();
        var latest = new Revision();
        Revision? revision = null;

        Walk(xml, (reader, isStart, text) =>
        {
            var name = reader.LocalName;
            if (isStart)
            {
                switch (name)
                {
                    case "folders":
                        inFolders = true;
                        break;
                    case "files":
                        inFiles = true;
                        break;
                    case "folder" when inFolders:
                        // A child directory. Trashed entries carry a `deleted` attribute.
                        var folderName = reader.GetAttribute("name");
                        if (reader.GetAttribute("deleted") is null && !string.IsNullOrEmpty(folderName))
                        {
                            var path = ChildPath(basePath, folderName);
                            items.Add(new CloudFileItem("d:" + path, folderName, path, isDirectory: true, 0, CloudFileItem.UnknownDate)
                            {
                                IsHidden = CloudFileItem.IsDotHidden(folderName),
                            });
                        }

                        break;
                    case "file" when inFiles:
                        fileName = reader.GetAttribute("name");
                        fileDeleted = reader.GetAttribute("deleted") is not null;
                        current = new Revision();
                        latest = new Revision();
                        revision = null;
                        break;
                    case "currentRevision" when inFiles:
                        revision = current;
                        break;
                    case "latestRevision" when inFiles:
                        revision = latest;
                        break;
                }

                return;
            }

            switch (name)
            {
                case "folders":
                    inFolders = false;
                    break;
                case "files":
                    inFiles = false;
                    break;
                case "currentRevision" or "latestRevision" when inFiles:
                    revision = null;
                    break;
                case "size" when inFiles:
                    (revision ?? current).Size = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0;
                    break;
                case "md5" when inFiles:
                    if (text.Length > 0)
                    {
                        (revision ?? current).Md5 = text;
                    }

                    break;
                case "modified" when inFiles:
                    if (text.Length > 0)
                    {
                        (revision ?? current).Modified = text;
                    }

                    break;
                case "state" when inFiles:
                    (revision ?? current).State = text;
                    break;
                case "file" when inFiles:
                    var chosen = current.State is not null ? current : latest;

                    // Only completed, non-trashed revisions are shown; incomplete or corrupt
                    // uploads are hidden.
                    if (!string.IsNullOrEmpty(fileName) && !fileDeleted &&
                        string.Equals(chosen.State, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = ChildPath(basePath, fileName);
                        items.Add(new CloudFileItem("f:" + path, fileName, path, isDirectory: false, chosen.Size, ParseDate(chosen.Modified), chosen.Md5)
                        {
                            IsHidden = CloudFileItem.IsDotHidden(fileName),
                        });
                    }

                    fileName = null;
                    break;
            }
        });

        return items;
    }

    /// <summary>
    /// A folder's own modified date from a direct folder GET: the top-level
    /// <c>&lt;modified&gt;</c> (depth 2), else a <c>time</c> attribute on the root <c>&lt;folder&gt;</c>.
    /// Child <c>&lt;folder&gt;</c> entries of a parent listing carry neither, which is why
    /// sub-folders otherwise have no date.
    /// </summary>
    public static DateTimeOffset? ParseFolderModified(string xml)
    {
        string? modified = null;
        string? timeAttribute = null;
        Walk(xml, (reader, isStart, text) =>
        {
            if (isStart)
            {
                if (reader.LocalName == "folder" && reader.Depth == 0)
                {
                    timeAttribute = reader.GetAttribute("time");
                }

                return;
            }

            // A file's <modified> sits far deeper (…>files>file>currentRevision).
            if (reader.LocalName == "modified" && reader.Depth == 1 && modified is null)
            {
                modified = text;
            }
        });

        if (!string.IsNullOrEmpty(modified))
        {
            return ParseDate(modified);
        }

        return string.IsNullOrEmpty(timeAttribute) ? null : ParseDate(timeAttribute);
    }

    /// <summary>
    /// Account-level capacity and usage — direct children of <c>&lt;user&gt;</c> only; per-device
    /// <c>&lt;usage&gt;</c> elements sit deeper and must not overwrite them. Capacity -1 means an
    /// unlimited plan; a missing capacity is reported the same way.
    /// </summary>
    public static AccountUsage? ParseAccountUsage(string xml)
    {
        long? capacity = null;
        long? usage = null;
        int? userDepth = null;
        Walk(xml, (reader, isStart, text) =>
        {
            if (isStart)
            {
                if (reader.LocalName == "user" && userDepth is null)
                {
                    userDepth = reader.Depth;
                }

                return;
            }

            if (userDepth is { } depth && reader.Depth == depth + 1)
            {
                if (reader.LocalName == "capacity" && capacity is null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                {
                    capacity = c;
                }
                else if (reader.LocalName == "usage" && usage is null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
                {
                    usage = u;
                }
            }
        });

        return usage is { } used ? new AccountUsage(capacity ?? -1, used) : null;
    }

    /// <summary>
    /// <c>&lt;publicURI&gt;</c>/<c>&lt;publicSharePath&gt;</c> of a file document, or null when the file
    /// carries no (non-empty) publicURI — i.e. isn't shared. Neither name occurs anywhere else in
    /// a JFS document, so a flat scan is enough; the first occurrence wins.
    /// </summary>
    public static PublicShare? ParsePublicShare(string xml)
    {
        var values = ScanFirst(xml, "publicURI", "publicSharePath");
        return values.TryGetValue("publicURI", out var uri) && uri.Length > 0
            ? new PublicShare(uri, values.GetValueOrDefault("publicSharePath") ?? string.Empty)
            : null;
    }

    /// <summary>JFS wraps a failure in <c>&lt;error&gt;</c> with a <c>&lt;message&gt;</c> and a <c>&lt;reason&gt;</c>.</summary>
    public static string? ParseError(string xml)
    {
        var values = ScanFirst(xml, "message", "reason");
        foreach (var key in new[] { "message", "reason" })
        {
            if (values.TryGetValue(key, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Revision timestamps use a non-standard dash before the time ("2024-01-15-T10:30:00Z").
    /// Normalised to ISO 8601 and parsed with or without fractional seconds and offset; no zone
    /// means UTC. Unparseable → <see cref="CloudFileItem.UnknownDate"/>.
    /// </summary>
    public static DateTimeOffset ParseDate(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return CloudFileItem.UnknownDate;
        }

        var normalized = trimmed.Replace("-T", "T", StringComparison.Ordinal);
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : CloudFileItem.UnknownDate;
    }

    private static string ChildPath(string basePath, string name)
        => basePath is "/" or "" ? "/" + name : basePath.TrimEnd('/') + "/" + name;

    private static Dictionary<string, string> ScanFirst(string xml, params string[] wanted)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(xml, (reader, isStart, text) =>
        {
            if (!isStart && wanted.Contains(reader.LocalName) && !found.ContainsKey(reader.LocalName))
            {
                found[reader.LocalName] = text;
            }
        });
        return found;
    }

    /// <summary>
    /// Forward-only walk. The callback sees every start element and, on the matching end, the
    /// trimmed text directly inside it (empty for elements with children).
    /// </summary>
    private static void Walk(string xml, Action<XmlReader, bool, string> visit)
    {
        var text = new System.Text.StringBuilder();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        text.Clear();
                        visit(reader, true, string.Empty);
                        if (reader.IsEmptyElement)
                        {
                            visit(reader, false, string.Empty);
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement:
                        visit(reader, false, text.ToString().Trim());
                        text.Clear();
                        break;
                }
            }
        }
        catch (XmlException)
        {
            // Keep what was parsed before the malformed part, as XMLParser on macOS does.
        }
    }

    private sealed class Revision
    {
        public long Size { get; set; }

        public string? Md5 { get; set; }

        public string? Modified { get; set; }

        public string? State { get; set; }
    }
}
