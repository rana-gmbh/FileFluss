// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FileFluss.Core.Providers.S3;

internal sealed record S3Object(string Key, long Size, DateTimeOffset? LastModified, string? ETag);

/// <summary>One page of a ListObjectsV2 response.</summary>
internal sealed class ListObjectsV2Page
{
    public List<S3Object> Contents { get; } = [];

    public List<string> CommonPrefixes { get; } = [];

    public string? NextContinuationToken { get; set; }
}

/// <summary>
/// Parsers for the S3 XML bodies (port of the macOS <c>S3XMLParsers</c>). Elements are matched
/// by local name, so the S3 namespace — present on AWS, absent on some compatible servers —
/// doesn't matter. DTDs are refused: the body comes from a server the user typed in.
/// </summary>
internal static class S3Xml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
    };

    /// <summary><c>ListAllMyBucketsResult</c> from <c>GET /</c>.</summary>
    public static IReadOnlyList<(string Name, DateTimeOffset? CreationDate)> ParseListBuckets(string xml)
    {
        var document = Load(xml) ?? throw CloudProviderException.InvalidResponse();
        var buckets = new List<(string, DateTimeOffset?)>();
        foreach (var bucket in document.Descendants().Where(e => e.Name.LocalName == "Bucket"))
        {
            var name = Child(bucket, "Name")?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                buckets.Add((name, ParseIso8601(Child(bucket, "CreationDate"))));
            }
        }

        return buckets;
    }

    /// <summary><c>ListBucketResult</c> of ListObjectsV2.</summary>
    public static ListObjectsV2Page ParseListObjectsV2(string xml)
    {
        var document = Load(xml) ?? throw CloudProviderException.InvalidResponse();
        var page = new ListObjectsV2Page();
        foreach (var element in document.Root?.Elements() ?? [])
        {
            switch (element.Name.LocalName)
            {
                case "Contents":
                    // Keys are taken verbatim — a key can legitimately start or end with spaces.
                    if (Child(element, "Key") is { } key)
                    {
                        var size = long.TryParse(Child(element, "Size")?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0;
                        page.Contents.Add(new S3Object(key, size, ParseIso8601(Child(element, "LastModified")), Child(element, "ETag")));
                    }

                    break;
                case "CommonPrefixes":
                    if (Child(element, "Prefix") is { Length: > 0 } prefix)
                    {
                        page.CommonPrefixes.Add(prefix);
                    }

                    break;
                case "NextContinuationToken":
                    var token = element.Value.Trim();
                    page.NextContinuationToken = token.Length == 0 ? null : token;
                    break;
            }
        }

        return page;
    }

    /// <summary>"Code: Message" from the standard <c>&lt;Error&gt;</c> body every S3 4xx/5xx carries; null when there is none.</summary>
    public static string? ParseError(string? xml)
    {
        var document = Load(xml);
        if (document is null)
        {
            return null;
        }

        var code = First(document, "Code");
        var message = First(document, "Message");
        return code is not null && message is not null ? $"{code}: {message}" : message ?? code;
    }

    /// <summary>The S3 error code (e.g. "AccessDenied") of an error body.</summary>
    public static string? ParseErrorCode(string? xml) => Load(xml) is { } document ? First(document, "Code") : null;

    /// <summary>
    /// True when a 200 response actually carries an <c>&lt;Error&gt;</c> — CopyObject reports
    /// failures that happen mid-copy this way.
    /// </summary>
    public static bool IsErrorDocument(string? xml) => Load(xml)?.Root?.Name.LocalName == "Error";

    /// <summary>The bucket's real region from a <c>PermanentRedirect</c> body (legacy responses without the header).</summary>
    public static string? ParseRegion(string? xml) => Load(xml) is { } document ? First(document, "Region")?.Trim() : null;

    public static DateTimeOffset? ParseIso8601(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    private static XDocument? Load(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Child(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string? First(XDocument document, string localName)
        => document.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
