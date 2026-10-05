// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text;
using System.Xml;

namespace FileFluss.Core.Mount;

/// <summary>A property name: namespace URI + local name.</summary>
internal readonly record struct DavProperty(string Namespace, string Name)
{
    public const string Dav = "DAV:";

    /// <summary>The namespace Windows' WebDAV redirector (and Office) put their Win32 properties in.</summary>
    public const string Microsoft = "urn:schemas-microsoft-com:";

    public static DavProperty D(string name) => new(Dav, name);

    public static DavProperty Z(string name) => new(Microsoft, name);
}

/// <summary>What a PROPFIND asked for.</summary>
internal enum PropfindKind
{
    AllProp,
    PropName,
    Prop,
}

internal sealed record PropfindRequest(PropfindKind Kind, IReadOnlyList<DavProperty> Properties)
{
    public static readonly PropfindRequest AllProp = new(PropfindKind.AllProp, []);
}

/// <summary>One resource in a multistatus answer.</summary>
internal sealed record DavResource(
    string Href,
    string ProviderPath,
    string DisplayName,
    bool IsCollection,
    long Size,
    DateTimeOffset Modified,
    DateTimeOffset Created,
    bool IsHidden,
    string ContentType);

/// <summary>Values that are the same for every resource in one response.</summary>
internal sealed record DavResponseContext(long? QuotaUsed, long? QuotaAvailable, Func<string, IReadOnlyList<DavLock>> LocksFor);

/// <summary>
/// The XML half of the server: parsing PROPFIND / PROPPATCH / LOCK bodies and rendering
/// multistatus answers. Request bodies come from any local process, so DTDs are prohibited
/// (no entity expansion, no external resolution).
/// </summary>
internal static class WebDavXml
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 4 * 1024 * 1024,
    };

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = new UTF8Encoding(false),
        Indent = false,
        OmitXmlDeclaration = false,
        CheckCharacters = true,
    };

    // MARK: - Parsing

    /// <summary>An empty body means allprop (RFC 4918 §9.1).</summary>
    public static PropfindRequest ParsePropfind(byte[] body)
    {
        if (body.Length == 0)
        {
            return PropfindRequest.AllProp;
        }

        var properties = new List<DavProperty>();
        var kind = PropfindKind.AllProp;
        using var reader = XmlReader.Create(new MemoryStream(body), ReaderSettings);
        var depth = -1;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.NamespaceURI == DavProperty.Dav)
            {
                switch (reader.LocalName)
                {
                    case "allprop":
                        kind = PropfindKind.AllProp;
                        continue;
                    case "propname":
                        kind = PropfindKind.PropName;
                        continue;
                    case "prop":
                        kind = PropfindKind.Prop;
                        depth = reader.Depth + 1;
                        continue;
                }
            }

            if (reader.Depth == depth)
            {
                properties.Add(new DavProperty(reader.NamespaceURI, reader.LocalName));
            }
        }

        return new PropfindRequest(kind, properties);
    }

    /// <summary>The properties a PROPPATCH sets (with their text) and removes.</summary>
    public static (List<(DavProperty Property, string Value)> Set, List<DavProperty> Remove) ParseProppatch(byte[] body)
    {
        var set = new List<(DavProperty, string)>();
        var remove = new List<DavProperty>();
        if (body.Length == 0)
        {
            return (set, remove);
        }

        using var reader = XmlReader.Create(new MemoryStream(body), ReaderSettings);
        bool? inSet = null;
        var propDepth = -1;

        // ReadInnerXml leaves the reader on the node after the property already; reading
        // again would skip the next property.
        var positioned = false;
        while (positioned || reader.Read())
        {
            positioned = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == DavProperty.Dav && reader.LocalName is "set" or "remove")
            {
                inSet = null;
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.NamespaceURI == DavProperty.Dav && reader.LocalName is "set" or "remove" && reader.Depth == 1)
            {
                inSet = reader.LocalName == "set";
                continue;
            }

            if (reader.NamespaceURI == DavProperty.Dav && reader.LocalName == "prop" && inSet is not null)
            {
                propDepth = reader.Depth + 1;
                continue;
            }

            if (inSet is { } isSet && reader.Depth == propDepth)
            {
                var property = new DavProperty(reader.NamespaceURI, reader.LocalName);
                if (isSet)
                {
                    var value = string.Empty;
                    if (!reader.IsEmptyElement)
                    {
                        value = reader.ReadInnerXml();
                        positioned = true;
                    }

                    set.Add((property, value));
                }
                else
                {
                    remove.Add(property);
                }
            }
        }

        return (set, remove);
    }

    /// <summary>The <c>&lt;D:owner&gt;</c> of a LOCK body as raw inner XML, or null.</summary>
    public static string? ParseLockOwner(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        using var reader = XmlReader.Create(new MemoryStream(body), ReaderSettings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == DavProperty.Dav && reader.LocalName == "owner")
            {
                return reader.IsEmptyElement ? null : reader.ReadInnerXml();
            }
        }

        return null;
    }

    // MARK: - Rendering

    /// <summary>
    /// The PROPFIND multistatus. Windows caps the whole body at <c>FileAttributesLimitInBytes</c>
    /// (1,000,000 bytes by default) and shows an over-limit folder as empty, so the XML is
    /// unindented, declares each namespace once on the root, answers only what was asked,
    /// and keeps the bulky lock properties out of allprop listings of many entries.
    /// </summary>
    public static byte[] RenderPropfind(IReadOnlyList<DavResource> resources, PropfindRequest request, DavResponseContext context)
    {
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, WriterSettings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("D", "multistatus", DavProperty.Dav);
            xml.WriteAttributeString("xmlns", "Z", null, DavProperty.Microsoft);
            var includeLocks = resources.Count == 1;
            foreach (var resource in resources)
            {
                xml.WriteStartElement("response", DavProperty.Dav);
                xml.WriteElementString("href", DavProperty.Dav, resource.Href);

                var found = new List<(DavProperty Property, Action<XmlWriter>? Write)>();
                var missing = new List<DavProperty>();
                IEnumerable<DavProperty> wanted = request.Kind == PropfindKind.Prop
                    ? request.Properties
                    : AllProperties(resource, includeLocks || request.Kind == PropfindKind.PropName);
                foreach (var property in wanted.Distinct())
                {
                    var writer = ValueWriter(property, resource, context);
                    if (writer is null)
                    {
                        missing.Add(property);
                    }
                    else
                    {
                        found.Add((property, request.Kind == PropfindKind.PropName ? null : writer));
                    }
                }

                if (found.Count > 0)
                {
                    xml.WriteStartElement("propstat", DavProperty.Dav);
                    xml.WriteStartElement("prop", DavProperty.Dav);
                    foreach (var (property, write) in found)
                    {
                        xml.WriteStartElement(property.Name, property.Namespace);
                        write?.Invoke(xml);
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                    xml.WriteElementString("status", DavProperty.Dav, "HTTP/1.1 200 OK");
                    xml.WriteEndElement();
                }

                if (missing.Count > 0)
                {
                    // Properties we don't have go into a 404 propstat, which every client —
                    // the redirector included — reads as "not available" rather than an error.
                    xml.WriteStartElement("propstat", DavProperty.Dav);
                    xml.WriteStartElement("prop", DavProperty.Dav);
                    foreach (var property in missing)
                    {
                        WriteEmpty(xml, property);
                    }

                    xml.WriteEndElement();
                    xml.WriteElementString("status", DavProperty.Dav, "HTTP/1.1 404 Not Found");
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The PROPPATCH answer: every property reported as set. macOS answered with an empty
    /// <c>&lt;D:prop/&gt;</c>; Windows matches the answer against what it sent and treats a
    /// property missing from the response as a failure, so each is echoed back with 200.
    /// </summary>
    public static byte[] RenderProppatch(string href, IEnumerable<DavProperty> properties)
    {
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, WriterSettings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("D", "multistatus", DavProperty.Dav);
            xml.WriteAttributeString("xmlns", "Z", null, DavProperty.Microsoft);
            xml.WriteStartElement("response", DavProperty.Dav);
            xml.WriteElementString("href", DavProperty.Dav, href);
            xml.WriteStartElement("propstat", DavProperty.Dav);
            xml.WriteStartElement("prop", DavProperty.Dav);
            foreach (var property in properties.Distinct())
            {
                WriteEmpty(xml, property);
            }

            xml.WriteEndElement();
            xml.WriteElementString("status", DavProperty.Dav, "HTTP/1.1 200 OK");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>The LOCK answer body: <c>&lt;D:prop&gt;&lt;D:lockdiscovery&gt;…</c>.</summary>
    public static byte[] RenderLock(DavLock davLock)
    {
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, WriterSettings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("D", "prop", DavProperty.Dav);
            xml.WriteStartElement("lockdiscovery", DavProperty.Dav);
            WriteActiveLock(xml, davLock);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>RFC 1123, as <c>getlastmodified</c> and the Win32 time properties use.</summary>
    public static string HttpDate(DateTimeOffset date) => date.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

    /// <summary>RFC 3339 in UTC, as <c>creationdate</c> uses.</summary>
    public static string IsoDate(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses the dates Windows sends in PROPPATCH (RFC 1123) and ISO 8601 for good measure.</summary>
    public static DateTimeOffset? ParseDate(string text)
    {
        var trimmed = text.Trim();
        if (DateTimeOffset.TryParseExact(trimmed, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rfc))
        {
            return rfc;
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any) ? any : null;
    }

    private static IEnumerable<DavProperty> AllProperties(DavResource resource, bool includeLocks)
    {
        yield return DavProperty.D("displayname");
        yield return DavProperty.D("creationdate");
        yield return DavProperty.D("getlastmodified");
        yield return DavProperty.D("getetag");
        yield return DavProperty.D("resourcetype");
        if (!resource.IsCollection)
        {
            yield return DavProperty.D("getcontentlength");
            yield return DavProperty.D("getcontenttype");
        }

        yield return DavProperty.Z("Win32FileAttributes");
        if (includeLocks)
        {
            // Advertising lock support keeps WebDAV clients happy (macOS: webdavfs_agent
            // concluded the share was read-only without it). Only on single-entry answers —
            // see RenderPropfind on Windows' response size cap.
            yield return DavProperty.D("supportedlock");
            yield return DavProperty.D("lockdiscovery");
        }
    }

    private static Action<XmlWriter>? ValueWriter(DavProperty property, DavResource resource, DavResponseContext context)
    {
        if (property.Namespace == DavProperty.Dav)
        {
            return property.Name switch
            {
                "displayname" => x => x.WriteString(XmlSafe(resource.DisplayName)),
                "creationdate" => x => x.WriteString(IsoDate(resource.Created)),
                "getlastmodified" => x => x.WriteString(HttpDate(resource.Modified)),
                "getetag" => x => x.WriteString(ETag(resource)),
                "resourcetype" => resource.IsCollection
                    ? x => x.WriteElementString("collection", DavProperty.Dav, string.Empty)
                    : _ => { },

                // A folder has no length; Windows asks anyway and copes with 0.
                "getcontentlength" => x => x.WriteString((resource.IsCollection ? 0 : resource.Size).ToString(CultureInfo.InvariantCulture)),
                "getcontenttype" when !resource.IsCollection => x => x.WriteString(resource.ContentType),
                "supportedlock" => WriteSupportedLock,
                "lockdiscovery" => x =>
                {
                    foreach (var active in context.LocksFor(resource.ProviderPath))
                    {
                        WriteActiveLock(x, active);
                    }
                },
                "quota-used-bytes" when context.QuotaUsed is { } used => x => x.WriteString(used.ToString(CultureInfo.InvariantCulture)),
                "quota-available-bytes" when context.QuotaAvailable is { } free => x => x.WriteString(free.ToString(CultureInfo.InvariantCulture)),

                // Pre-RFC Microsoft clients ask for these in the DAV: namespace.
                "iscollection" => x => x.WriteString(resource.IsCollection ? "1" : "0"),
                "ishidden" => x => x.WriteString(resource.IsHidden ? "1" : "0"),
                "isreadonly" => x => x.WriteString("0"),
                _ => null,
            };
        }

        if (property.Namespace == DavProperty.Microsoft)
        {
            return property.Name switch
            {
                "Win32FileAttributes" => x => x.WriteString(Win32Attributes(resource).ToString("x8", CultureInfo.InvariantCulture)),
                "Win32CreationTime" => x => x.WriteString(HttpDate(resource.Created)),
                "Win32LastModifiedTime" => x => x.WriteString(HttpDate(resource.Modified)),

                // Cloud storage has no access time; the modification time is the honest stand-in.
                "Win32LastAccessTime" => x => x.WriteString(HttpDate(resource.Modified)),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// FILE_ATTRIBUTE_DIRECTORY (0x10) for folders, FILE_ATTRIBUTE_ARCHIVE (0x20, what IIS
    /// reports) for files, plus FILE_ATTRIBUTE_HIDDEN (0x2) for dot-files — FileFluss treats
    /// a leading dot as hidden in every cloud listing, and Explorer then honours its own
    /// "Show hidden items" switch for them. Never read-only: that would stop Explorer writing.
    /// </summary>
    internal static uint Win32Attributes(DavResource resource)
        => (resource.IsCollection ? 0x10u : 0x20u) | (resource.IsHidden ? 0x2u : 0u);

    /// <summary>
    /// A stable entity tag per (path, mtime, size) — clients use it to notice remote changes
    /// between visits. FNV-1a rather than <c>string.GetHashCode</c>, which is randomised per
    /// process and would change every tag after a restart.
    /// </summary>
    internal static string ETag(DavResource resource)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(resource.ProviderPath))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return string.Create(CultureInfo.InvariantCulture, $"\"{hash:x16}-{resource.Modified.ToUnixTimeSeconds():x}-{resource.Size:x}\"");
    }

    private static void WriteEmpty(XmlWriter xml, DavProperty property)
    {
        xml.WriteStartElement(property.Name, property.Namespace.Length == 0 ? string.Empty : property.Namespace);
        xml.WriteEndElement();
    }

    private static void WriteSupportedLock(XmlWriter xml)
    {
        foreach (var scope in new[] { "exclusive", "shared" })
        {
            xml.WriteStartElement("lockentry", DavProperty.Dav);
            xml.WriteStartElement("lockscope", DavProperty.Dav);
            xml.WriteElementString(scope, DavProperty.Dav, string.Empty);
            xml.WriteEndElement();
            xml.WriteStartElement("locktype", DavProperty.Dav);
            xml.WriteElementString("write", DavProperty.Dav, string.Empty);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
    }

    private static void WriteActiveLock(XmlWriter xml, DavLock davLock)
    {
        xml.WriteStartElement("activelock", DavProperty.Dav);
        xml.WriteStartElement("locktype", DavProperty.Dav);
        xml.WriteElementString("write", DavProperty.Dav, string.Empty);
        xml.WriteEndElement();
        xml.WriteStartElement("lockscope", DavProperty.Dav);
        xml.WriteElementString(davLock.Exclusive ? "exclusive" : "shared", DavProperty.Dav, string.Empty);
        xml.WriteEndElement();
        xml.WriteElementString("depth", DavProperty.Dav, davLock.Depth);
        xml.WriteStartElement("owner", DavProperty.Dav);
        if (davLock.OwnerXml is { Length: > 0 } owner)
        {
            try
            {
                xml.WriteRaw(SanitizeOwner(owner));
            }
            catch (ArgumentException)
            {
            }
        }

        xml.WriteEndElement();
        xml.WriteElementString("timeout", DavProperty.Dav, "Second-" + davLock.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        xml.WriteStartElement("locktoken", DavProperty.Dav);
        xml.WriteElementString("href", DavProperty.Dav, davLock.Token);
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    /// <summary>
    /// The owner is echoed back as the client sent it (Office shows it in "locked by"). It is
    /// re-parsed first so a malformed fragment can't break the response document.
    /// </summary>
    private static string SanitizeOwner(string ownerXml)
    {
        try
        {
            var settings = ReaderSettings.Clone();
            settings.ConformanceLevel = ConformanceLevel.Fragment;
            using var reader = XmlReader.Create(new StringReader(ownerXml), settings);
            while (reader.Read())
            {
            }

            return ownerXml;
        }
        catch (XmlException)
        {
            return System.Security.SecurityElement.Escape(ownerXml) ?? string.Empty;
        }
    }

    /// <summary>Characters XML 1.0 can't carry (control characters in a cloud file name) become U+FFFD.</summary>
    private static string XmlSafe(string text)
    {
        if (text.All(XmlConvert.IsXmlChar))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                builder.Append(text[i]).Append(text[++i]);
            }
            else
            {
                builder.Append(XmlConvert.IsXmlChar(text[i]) ? text[i] : '�');
            }
        }

        return builder.ToString();
    }
}
