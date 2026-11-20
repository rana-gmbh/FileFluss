// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text;
using System.Xml;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.WebDav;

/// <summary>
/// One <c>&lt;d:response&gt;</c> of a PROPFIND/SEARCH multistatus body.
/// <see cref="Name"/> is empty for the collection the base URL points at (the root), which
/// listings drop.
/// </summary>
internal sealed record WebDavEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModificationDate,
    string? Checksum,
    string? ContentType)
{
    public CloudFileItem ToItem() => ToItem(Path, Name);

    /// <summary>The item under a path the caller knows to be right (listing parent + href name).</summary>
    public CloudFileItem ToItem(string path, string name)
        => new(path, name, path, IsDirectory, Size, ModificationDate, Checksum)
        {
            IsHidden = CloudFileItem.IsDotHidden(name),
        };
}

/// <summary>
/// Multistatus parsing shared by WebDAV, NextCloud and GMX (the macOS
/// <c>WebDAVResponseParser</c>).
///
/// <para>Element names are matched on their <b>local name only</b>, so whatever prefix the
/// server binds to <c>DAV:</c> (<c>d:</c>, <c>D:</c>, Apache's <c>lp1:</c>, a default
/// namespace, none at all) parses the same, and Nextcloud's <c>oc:size</c> /
/// <c>oc:checksums</c> are picked up without caring which namespace they came in.</para>
///
/// <para>Like the macOS <c>XMLParser</c> delegate it replaces, a body that turns malformed
/// half-way keeps the responses parsed before the error rather than failing the listing.</para>
/// </summary>
internal static class WebDavResponseParser
{
    private static readonly XmlReaderSettings Settings = new()
    {
        // A server's body is untrusted: never resolve external entities or DTDs.
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    public static List<WebDavEntry> Parse(string xml, string davBaseUrl)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return Parse(stream, davBaseUrl);
    }

    /// <param name="davBaseUrl">The account's DAV root; its path is stripped from every href.</param>
    public static List<WebDavEntry> Parse(Stream body, string davBaseUrl)
    {
        var basePath = BasePathOf(davBaseUrl);
        var entries = new List<WebDavEntry>();
        var open = new Stack<string>();
        var text = new StringBuilder();
        var current = new ResponseState();

        void End(string local, string? parent)
        {
            var value = text.ToString().Trim();
            switch (local)
            {
                case "href" when parent == "response":
                    current.Href = value;
                    break;

                // Servers list properties they don't have as empty elements in a 404
                // propstat; an empty value never overwrites one that was found.
                case "displayname" when value.Length > 0:
                    current.DisplayName = value;
                    break;
                case "getlastmodified" when value.Length > 0:
                    current.LastModified = value;
                    break;
                case "getcontentlength" when value.Length > 0:
                    current.ContentLength = value;
                    break;
                case "getcontenttype" when value.Length > 0:
                    current.ContentType = value;
                    break;

                // Nextcloud: <oc:checksums><oc:checksum>SHA1:… MD5:…</oc:checksum></oc:checksums>
                case "checksums" when value.Length > 0:
                    current.Checksum = value;
                    break;

                // Nextcloud/ownCloud folder size (<oc:size>); plain WebDAV has none for folders.
                case "size" when value.Length > 0:
                    current.OcSize = value;
                    break;
                case "response":
                    if (Build(current, basePath) is { } entry)
                    {
                        entries.Add(entry);
                    }

                    break;
            }
        }

        try
        {
            using var reader = XmlReader.Create(body, Settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var local = reader.LocalName;
                        var parent = open.Count > 0 ? open.Peek() : null;
                        text.Clear();
                        if (local == "response")
                        {
                            current = new ResponseState();
                        }
                        else if (local == "collection")
                        {
                            current.IsDirectory = true;
                        }

                        if (reader.IsEmptyElement)
                        {
                            End(local, parent);
                        }
                        else
                        {
                            open.Push(local);
                        }

                        break;
                    }

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        text.Append(reader.Value);
                        break;

                    case XmlNodeType.EndElement:
                    {
                        if (open.Count > 0)
                        {
                            open.Pop();
                        }

                        End(reader.LocalName, open.Count > 0 ? open.Peek() : null);
                        break;
                    }
                }
            }
        }
        catch (XmlException e)
        {
            // macOS 1.6: whatever was read before the error is only part of the folder, and must
            // not pass for all of it — a folder that merely looks emptier than it is tells Mirror
            // to delete the rest from the other side.
            throw CloudProviderException.CommandFailed(Localization.L("The server sent a folder listing FileFluss couldn't read."), e);
        }

        return entries;
    }

    /// <summary>
    /// RFC 4331 <c>quota-used-bytes</c> / <c>quota-available-bytes</c>. Null when the server
    /// doesn't report usage (RFC 4331 unsupported), so the status bar simply omits the line.
    /// <c>quota-available-bytes</c> is FREE space, not the total — total = used + available.
    /// </summary>
    public static CloudStorageQuota? ParseQuota(string xml)
    {
        long? used = null;
        long? available = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            reader.Read();
            while (!reader.EOF)
            {
                // ReadElementContentAsString already moves to the next node; reading again
                // would skip a sibling written without whitespace in between.
                if (reader is { NodeType: XmlNodeType.Element, IsEmptyElement: false, LocalName: "quota-used-bytes" })
                {
                    used = ParseLong(reader.ReadElementContentAsString());
                }
                else if (reader is { NodeType: XmlNodeType.Element, IsEmptyElement: false, LocalName: "quota-available-bytes" })
                {
                    available = ParseLong(reader.ReadElementContentAsString());
                }
                else
                {
                    reader.Read();
                }
            }
        }
        catch (XmlException)
        {
        }

        if (used is not { } usedBytes)
        {
            return null;
        }

        // Nextcloud/ownCloud (very often behind a "plain WebDAV" account) report negative
        // sentinels here: -1 not computed, -2 unknown, -3 unlimited. Adding those to the used
        // figure would invent a total smaller than what's used and make the free-space
        // pre-flight refuse every upload, so a negative value means "no known total".
        long? total = available is { } free && free >= 0 ? usedBytes + free : null;
        return new CloudStorageQuota(usedBytes, total);
    }

    /// <summary>
    /// HTTP dates as WebDAV servers send them: RFC 1123 (<c>Tue, 07 Jan 2025 10:00:00 GMT</c>),
    /// plus the variants seen in the wild (single-digit day, numeric zone, ISO 8601).
    /// </summary>
    public static DateTimeOffset? ParseHttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces;
        if (DateTimeOffset.TryParseExact(trimmed, "r", CultureInfo.InvariantCulture, styles, out var rfc1123))
        {
            return rfc1123;
        }

        string[] formats =
        [
            "ddd, d MMM yyyy HH':'mm':'ss 'GMT'",
            "ddd, d MMM yyyy HH':'mm':'ss 'UTC'",
            "ddd, d MMM yyyy HH':'mm':'ss zzz",
            "dddd, dd-MMM-yy HH':'mm':'ss 'GMT'",
        ];
        if (DateTimeOffset.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, styles, out var variant))
        {
            return variant;
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, styles, out var loose) ? loose : null;
    }

    /// <summary>RFC 1123 in GMT, for PROPPATCH <c>getlastmodified</c>.</summary>
    public static string FormatHttpDate(DateTimeOffset date)
        => date.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);

    /// <summary>The percent-decoded path of the DAV base URL, without a trailing slash ("" for a bare host).</summary>
    public static string BasePathOf(string davBaseUrl)
    {
        var path = Uri.TryCreate(davBaseUrl, UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath)
            : davBaseUrl;
        return path.TrimEnd('/');
    }

    private static WebDavEntry? Build(ResponseState state, string basePath)
    {
        if (state.Href is not { } href)
        {
            return null;
        }

        var relativePath = RelativePath(HrefPath(href), basePath);
        if (relativePath.Length > 1 && relativePath.EndsWith('/'))
        {
            relativePath = relativePath.TrimEnd('/');
            if (relativePath.Length == 0)
            {
                relativePath = "/";
            }
        }

        var lastComponent = relativePath == "/" ? string.Empty : relativePath[(relativePath.LastIndexOf('/') + 1)..];
        var resolvedName = !string.IsNullOrEmpty(state.DisplayName) ? state.DisplayName : lastComponent;
        var name = resolvedName.Trim('/');

        var size = state.IsDirectory
            ? ParseLong(state.OcSize) ?? 0
            : ParseLong(state.ContentLength) ?? 0;

        return new WebDavEntry(
            relativePath,
            name,
            state.IsDirectory,
            size,
            ParseHttpDate(state.LastModified) ?? CloudFileItem.UnknownDate,
            state.Checksum,
            state.ContentType);
    }

    /// <summary>
    /// The decoded path of an href. Absolute hrefs ("https://host/dav/a%20b") are reduced to
    /// their path before decoding — decoding first would leave a URL with spaces that no
    /// longer parses.
    /// </summary>
    private static string HrefPath(string href)
    {
        var path = href;
        if ((href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }

        return Uri.UnescapeDataString(path);
    }

    private static string RelativePath(string hrefPath, string basePath)
    {
        if (basePath.Length > 0)
        {
            // Case-insensitive fallback for servers (IIS) that echo the base in another case.
            foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            {
                if (!hrefPath.StartsWith(basePath, comparison))
                {
                    continue;
                }

                var rest = hrefPath[basePath.Length..];
                if (rest.Length == 0)
                {
                    return "/";
                }

                // Only strip at a component boundary: base "/dav" must not eat "/davx/file".
                if (rest[0] == '/')
                {
                    return rest;
                }
            }
        }

        return hrefPath.StartsWith('/') ? hrefPath : "/" + hrefPath;
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private sealed class ResponseState
    {
        public string? Href { get; set; }

        public string? DisplayName { get; set; }

        public string? LastModified { get; set; }

        public string? ContentLength { get; set; }

        public string? ContentType { get; set; }

        public string? Checksum { get; set; }

        public string? OcSize { get; set; }

        public bool IsDirectory { get; set; }
    }
}
