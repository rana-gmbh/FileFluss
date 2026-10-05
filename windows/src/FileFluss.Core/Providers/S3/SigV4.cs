// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// AWS Signature Version 4: the <c>Authorization</c> header for API calls and query-string
/// signing for presigned share links. Pure functions with the clock injected, so they can be
/// pinned against AWS's published examples — a link signed wrong only fails when the
/// <i>recipient</i> opens it, which is a miserable way to find out.
/// </summary>
internal static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>SHA-256 of the empty body, the payload hash of every bodiless request.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>The documented payload hash for presigned GETs.</summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>SigV4 caps a presigned URL at 7 days.</summary>
    public static readonly TimeSpan MaximumPresignedExpiry = TimeSpan.FromDays(7);

    /// <summary>"20130524T000000Z".</summary>
    public static string AmzDate(DateTimeOffset now)
        => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>"20130524".</summary>
    public static string DateStamp(DateTimeOffset now)
        => now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// AWS URI encoding of one value: RFC 3986 unreserved characters (A–Z a–z 0–9 - . _ ~) stay,
    /// everything else becomes upper-case %XX of its UTF-8 bytes — which is exactly what
    /// <see cref="Uri.EscapeDataString"/> produces.
    /// </summary>
    public static string Encode(string value) => Uri.EscapeDataString(value);

    /// <summary>Encodes an object key (or "/"-prefixed path) segment by segment, keeping "/" so the virtual-folder structure survives.</summary>
    public static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// The canonical query string: names and values encoded, sorted by name then value
    /// (byte order of the encoded form), a parameter without value written as "name=".
    /// Requests send exactly this string, so what is signed is what goes on the wire.
    /// </summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> parameters)
        => string.Join('&', parameters
            .Select(p => (Name: Encode(p.Key), Value: Encode(p.Value)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value));

    /// <summary>
    /// Builds the canonical request. <paramref name="headers"/> are the headers to sign;
    /// names are lower-cased, values trimmed with inner runs of spaces collapsed, and the
    /// list sorted, as the specification requires.
    /// </summary>
    public static string CanonicalRequest(
        string method,
        string canonicalUri,
        string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers,
        string payloadHash,
        out string signedHeaders)
    {
        var canonical = headers
            .Select(h => (Name: h.Key.Trim().ToLowerInvariant(), Value: CanonicalHeaderValue(h.Value)))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();
        signedHeaders = string.Join(';', canonical.Select(h => h.Name));
        var headerBlock = string.Concat(canonical.Select(h => $"{h.Name}:{h.Value}\n"));
        return string.Join('\n', method, canonicalUri, canonicalQuery, headerBlock, signedHeaders, payloadHash);
    }

    public static string StringToSign(DateTimeOffset now, string credentialScope, string canonicalRequest)
        => string.Join('\n', Algorithm, AmzDate(now), credentialScope, Sha256Hex(canonicalRequest));

    public static string CredentialScope(DateTimeOffset now, string region, string service)
        => $"{DateStamp(now)}/{region}/{service}/aws4_request";

    /// <summary>kSigning = HMAC(HMAC(HMAC(HMAC("AWS4" + secret, date), region), service), "aws4_request").</summary>
    public static byte[] SigningKey(string secretAccessKey, string dateStamp, string region, string service)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    public static string Signature(string secretAccessKey, DateTimeOffset now, string region, string service, string stringToSign)
        => Convert.ToHexStringLower(Hmac(SigningKey(secretAccessKey, DateStamp(now), region, service), stringToSign));

    /// <summary>
    /// The <c>Authorization</c> header value for a request. The caller sends the same
    /// <c>x-amz-date</c> (<see cref="AmzDate"/> of <paramref name="now"/>) and
    /// <c>x-amz-content-sha256</c> it passed in <paramref name="headers"/>.
    /// </summary>
    public static string Authorization(
        string method,
        string canonicalUri,
        string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers,
        string payloadHash,
        string accessKeyId,
        string secretAccessKey,
        string region,
        string service,
        DateTimeOffset now)
    {
        var canonicalRequest = CanonicalRequest(method, canonicalUri, canonicalQuery, headers, payloadHash, out var signedHeaders);
        var scope = CredentialScope(now, region, service);
        var signature = Signature(secretAccessKey, now, region, service, StringToSign(now, scope, canonicalRequest));
        return $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    /// <summary>
    /// A presigned GET URL for <paramref name="key"/> on <paramref name="rawHost"/>. Nothing is
    /// sent: the signature is computed locally, and the URL streams the object to whoever has
    /// it until it expires.
    /// </summary>
    /// <param name="rawHost">
    /// Lower-cased before signing. Host names are case-insensitive and every HTTP client —
    /// browsers included — lower-cases them before sending; SigV4 signs the host as the server
    /// sees it, so a mixed-case bucket (Backblaze allows them, AWS does not) would otherwise
    /// give the recipient SignatureDoesNotMatch.
    /// </param>
    /// <param name="attachmentFilename">
    /// When set, adds <c>response-content-disposition: attachment; filename="…"</c> so the
    /// browser saves the file under its own name instead of rendering it inline. Like every
    /// other parameter it travels in the query string and is part of the signature.
    /// </param>
    public static Uri PresignedGetUrl(
        string rawHost,
        string key,
        string region,
        string accessKeyId,
        string secretAccessKey,
        int expiresInSeconds,
        string? attachmentFilename,
        DateTimeOffset now)
    {
        var host = rawHost.ToLowerInvariant();
        var scope = CredentialScope(now, region, "s3");
        var query = new List<KeyValuePair<string, string>>
        {
            new("X-Amz-Algorithm", Algorithm),
            new("X-Amz-Credential", $"{accessKeyId}/{scope}"),
            new("X-Amz-Date", AmzDate(now)),
            new("X-Amz-Expires", expiresInSeconds.ToString(CultureInfo.InvariantCulture)),
            new("X-Amz-SignedHeaders", "host"),
        };
        if (attachmentFilename is not null)
        {
            query.Add(new("response-content-disposition", $"attachment; filename=\"{attachmentFilename}\""));
        }

        var canonicalQuery = CanonicalQuery(query);
        var canonicalUri = EncodePath("/" + key);
        var canonicalRequest = CanonicalRequest("GET", canonicalUri, canonicalQuery, [new("host", host)], UnsignedPayload, out _);
        var signature = Signature(secretAccessKey, now, region, "s3", StringToSign(now, scope, canonicalRequest));
        return new Uri($"https://{host}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}");
    }

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>SHA-256 of a file, streamed — the payload hash of a single-PUT upload.</summary>
    public static async Task<string> Sha256HexOfFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = Net.HttpTransfer.OpenForUpload(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static byte[] Hmac(byte[] key, string message) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));

    /// <summary>Trim, and collapse runs of spaces to one (the spec's "Trimall").</summary>
    private static string CanonicalHeaderValue(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("  ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        var previousSpace = false;
        foreach (var c in trimmed)
        {
            if (c == ' ' && previousSpace)
            {
                continue;
            }

            builder.Append(c);
            previousSpace = c == ' ';
        }

        return builder.ToString();
    }
}
