// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.RegularExpressions;

namespace FileFluss.Core.Diagnostics;

/// <summary>
/// Last line of defence for the support log, the debug log mirror and the crash log: masks
/// what looks like a credential before a line is stored. Providers must still never log
/// secrets — but a server's error body may echo a token, an exception message may carry a
/// pre-signed URL, and a user hands the saved log to strangers on GitHub.
/// </summary>
public static partial class LogRedactor
{
    public const string Mask = "<redacted>";

    /// <summary><paramref name="text"/> with tokens, passwords, signatures and URL credentials masked.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = AuthorizationScheme().Replace(text, m => m.Groups[1].Value + " " + Mask);
        result = SecretHeader().Replace(result, m => m.Groups[1].Value + ": " + Mask);
        result = SecretJson().Replace(result, m => "\"" + m.Groups[1].Value + "\":\"" + Mask + "\"");
        result = SecretParameter().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + "=" + Mask);
        result = UrlCredentials().Replace(result, m => m.Groups[1].Value + Mask + "@");
        return result;
    }

    /// <summary>Longest message the support log keeps whole; a server's HTML error page can be 50 KB.</summary>
    public const int MaxMessageLength = 8_000;

    /// <summary>
    /// <paramref name="text"/> cut to <see cref="MaxMessageLength"/>, saying how much was left
    /// out. Apply after <see cref="Redact"/>: cutting first could split a token so that only its
    /// unmasked half stayed in the log.
    /// </summary>
    public static string Cap(string text)
        => text.Length <= MaxMessageLength
            ? text
            : string.Concat(text.AsSpan(0, MaxMessageLength), $"… ({text.Length - MaxMessageLength} more characters)");

    /// <summary>
    /// "Bearer eyJ…", "Basic dXNlcjpwYXNz" — a credential has a digit, a capital or a symbol
    /// in it, which keeps prose such as "Basic authentication" readable.
    /// </summary>
    [GeneratedRegex(@"\b((?i:Bearer|Basic|Digest))\s+(?=[A-Za-z0-9\-._~+/=]*[0-9A-Z\-._~+/=])[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    /// <summary>"Authorization: …", "Cookie: …", "X-Auth-Token: …" up to the end of the line.</summary>
    [GeneratedRegex(@"\b(Authorization|Proxy-Authorization|Cookie|Set-Cookie|X-Auth-Token|X-Api-Key|X-Amz-Security-Token|X-Storage-Token|X-SYNO-TOKEN|X-SEAFILE-OTP|Dropbox-API-Select-User)\s*:\s*[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretHeader();

    /// <summary>A JSON member whose name says it holds a secret.</summary>
    [GeneratedRegex(@"""((?:access|refresh|id|auth|session|upload|api|bearer|csrf)?_?token|accessToken|refreshToken|idToken|authToken|sessionToken|password|passwd|client_secret|clientSecret|secret|api_?key|apiKey|private_?key|privateKey|masterKey|master_key|sid|_sid|csid|tsid|authSid|session_?id|sessionId|auth|synotoken|qtoken|vtoken|personalToken|secretAccessKey|uploadKey)""\s*:\s*""(?:[^""\\]|\\.)*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretJson();

    /// <summary>A query, form or "name=value" parameter whose name says it holds a secret.</summary>
    [GeneratedRegex(@"(^|[?&;,\s""'(])((?:access_|refresh_|id_|auth_|session_)?token|authtoken|auth|password|passwd|pwd|secret|client_secret|code|code_verifier|api_?key|key|sig|signature|x-amz-signature|x-amz-credential|x-amz-security-token|x-goog-signature|x-goog-credential|sid|_sid|csid|tsid|session_?id|upload_id|synotoken|qtoken|vtoken)=([^&\s""'#,;)]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParameter();

    /// <summary>"https://user:password@host" — user info with a password.</summary>
    [GeneratedRegex(@"\b([a-z][a-z0-9+.\-]*://)[^/\s:@]+:[^/\s@]*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();
}
