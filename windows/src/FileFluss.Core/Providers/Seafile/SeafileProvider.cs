// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Seafile;

/// <summary>
/// Seafile (cloud and self-hosted). The password is exchanged once for a long-lived API token
/// via <c>/api2/auth-token/</c>, with an optional 2FA code; the password is then discarded.
///
/// <para><b>Path model.</b> The Seafile API is library-scoped: every path lives under a repo id.
/// FileFluss expects one "/" root per account, so libraries appear as top-level folders:</para>
/// <code>
///   /                         → the libraries
///   /&lt;library-name&gt;           → root of that library
///   /&lt;library-name&gt;/foo/bar   → /foo/bar inside it
/// </code>
/// <para>Two libraries with the same name are told apart as "&lt;name&gt; (&lt;id-prefix&gt;)".</para>
/// </summary>
[CloudProvider(CloudProviderType.Seafile)]
public sealed class SeafileProvider : CloudProvider
{
    private const string LogCategory = "seafile";

    private readonly HttpMessageHandler? _handler;
    private readonly Lock _gate = new();
    private Session? _session;

    /// <summary>Display name → library, built from the last library listing so a path resolves without a round trip per step.</summary>
    private Dictionary<string, SeafileLibrary> _libraries = new(StringComparer.Ordinal);

    public SeafileProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal SeafileProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _handler = handler;
        var stored = Credentials.Load<SeafileCredentials>(CredentialKey);
        if (stored is { ServerUrl.Length: > 0, Token.Length: > 0 })
        {
            _session = new Session(stored, CreateClient(stored.AllowSelfSignedCertificate ? HostOf(stored.ServerUrl) : null));
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Seafile;

    protected override string CredentialKeyPrefix => "seafile";

    public override bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>
    /// The full set — but these are the server's capabilities only: an admin can disable share
    /// links or force a password or expiry, and creation then fails with the server's message.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = true,
        CanRemove = true,
        SupportsPassword = true,
        SupportsExpiry = true,
        SupportsDownloadToggle = true,
    };

    public override ConnectSpec ConnectSpec => new(
        new ConnectField("serverUrl", ConnectFieldKind.Url, "Server URL") { Placeholder = "Server URL (e.g. https://seafile.example.com)" },
        // Seafile logins are usually e-mail addresses, but LDAP accounts use plain user names.
        new ConnectField("email", ConnectFieldKind.Text, "Email"),
        new ConnectField("password", ConnectFieldKind.Password, "Password"),
        new ConnectField("otp", ConnectFieldKind.OneTimeCode, "Two-factor code (only if enabled)") { Required = false },
        new ConnectField("allowSelfSigned", ConnectFieldKind.Checkbox, "Allow self-signed certificate")
        {
            Required = false,
            DefaultValue = "false",
            Help = "Enable when the server presents a self-signed or LAN-only TLS certificate. Trust is scoped to the host you entered.",
        },
        ConnectField.Info("Seafile doesn't support browser OAuth — the password is exchanged once for a long-lived API token, then discarded. Self-hosted servers work too."));

    private Session Current
    {
        get
        {
            lock (_gate)
            {
                return _session ?? throw CloudProviderException.NotAuthenticated();
            }
        }
    }

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
    {
        lock (_gate)
        {
            if (_session is not { } session)
            {
                return new Dictionary<string, string>();
            }

            return new Dictionary<string, string>
            {
                ["serverUrl"] = session.Credentials.ServerUrl,
                ["email"] = session.Credentials.Username,
                ["allowSelfSigned"] = session.Credentials.AllowSelfSignedCertificate ? "true" : "false",
            };
        }
    }

    // MARK: - Connecting

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var server = NormalizeServerUrl(values.GetTrimmed("serverUrl"));
        if (HostOf(server) is not { Length: > 0 } host)
        {
            throw CloudProviderException.CommandFailed(Localization.L("Server URL is not a valid URL."));
        }

        var username = values.GetTrimmed("email");
        var password = values.Get("password");
        var otp = values.GetOptional("otp");
        var allowSelfSigned = values.GetBool("allowSelfSigned");

        // The auth call trusts exactly what the live session will, otherwise a self-signed
        // server would fail the very first request.
        var http = CreateClient(allowSelfSigned ? host : null);
        var adopted = false;
        try
        {
            string token;
            try
            {
                token = await ObtainTokenAsync(http, server, username, password, otp, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.TwoFactorRequired && otp is null)
            {
                // The account has 2FA but the optional code field was left empty: ask for it
                // instead of making the user retype the password.
                var code = await prompt.RequestCodeAsync(
                    Localization.L("Two-factor code (only if enabled)"),
                    Localization.L("Two-factor authentication required. Enter your 6-digit code and try again."),
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(code))
                {
                    throw;
                }

                token = await ObtainTokenAsync(http, server, username, password, code.Trim(), cancellationToken).ConfigureAwait(false);
            }

            var credentials = new SeafileCredentials(server, username, token) { AllowSelfSignedCertificate = allowSelfSigned };
            Credentials.Save(CredentialKey, credentials);
            lock (_gate)
            {
                // The previous client may still serve an in-flight request; it is left to the GC.
                _session = new Session(credentials, http);
                _libraries = new Dictionary<string, SeafileLibrary>(StringComparer.Ordinal);
            }

            adopted = true;
            SupportLogger.Shared.Log($"Authenticated as {username}", LogCategory);
            return new ConnectResult(username);
        }
        finally
        {
            if (!adopted)
            {
                http.Dispose();
            }
        }
    }

    public override Task DisconnectAsync()
    {
        lock (_gate)
        {
            _session = null;
            _libraries = new Dictionary<string, SeafileLibrary>(StringComparer.Ordinal);
        }

        return base.DisconnectAsync();
    }

    /// <summary>
    /// <c>POST /api2/auth-token/</c>. A 403 carrying <c>X-SEAFILE-OTP: required</c> means the
    /// account wants a two-factor code; every other refusal is reported as bad credentials.
    /// </summary>
    private static async Task<string> ObtainTokenAsync(HttpClient http, string server, string username, string password, string? otp, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, server + "/api2/auth-token/")
        {
            Content = CloudHttp.FormContent(("username", username), ("password", password)),
        };
        if (!string.IsNullOrEmpty(otp))
        {
            request.Headers.TryAddWithoutValidation("X-SEAFILE-OTP", otp);
        }

        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-SEAFILE-OTP", out var otpHeader) &&
            otpHeader.Any(v => string.Equals(v.Trim(), "required", StringComparison.OrdinalIgnoreCase)))
        {
            throw CloudProviderException.TwoFactorRequired();
        }

        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"auth-token failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            throw CloudProviderException.InvalidCredentials();
        }

        var parsed = await response.ReadJsonAsync<SeafileTokenResponse>(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(parsed.Token) ? throw CloudProviderException.InvalidResponse() : parsed.Token;
    }

    /// <summary>Trims, drops a trailing slash and defaults a missing scheme to https: "example.com" → "https://example.com".</summary>
    internal static string NormalizeServerUrl(string raw)
    {
        var value = raw.Trim();
        while (value.EndsWith('/'))
        {
            value = value[..^1];
        }

        if (value.Length > 0 &&
            !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return value;
    }

    private static string? HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : null;

    private HttpClient CreateClient(string? trustedSelfSignedHost)
        => _handler is null
            ? CloudHttp.Create(trustedSelfSignedHost)
            : new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    // MARK: - Request plumbing

    private static HttpRequestMessage Request(Session session, HttpMethod method, string pathAndQuery, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, session.Credentials.ServerUrl + pathAndQuery) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", session.Credentials.Token);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    /// <summary>An API call; returns the body, or throws the mapped error.</summary>
    private static async Task<string> SendAsync(Session session, HttpMethod method, string pathAndQuery, string path, CancellationToken cancellationToken, HttpContent? content = null)
    {
        using var request = Request(session, method, pathAndQuery, content);
        using var response = await session.Http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, path);
        }

        return body;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw CloudProviderException.Network(e);
        }
    }

    private static T Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, CloudHttp.Json) ?? throw CloudProviderException.InvalidResponse();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    internal static string? ErrorMessage(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<SeafileErrorBody>(body, CloudHttp.Json)?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The macOS mapping (401 sign in again, 404 not found, 429, 507), plus Seafile's own
    /// statuses: 440 means an encrypted library whose password isn't set for this session, 443
    /// is "out of quota". A 400/403 with an error body says what went wrong (a read-only share,
    /// an invalid name), which beats a re-auth banner or a bare status code.
    /// </summary>
    internal static CloudProviderException MapError(HttpStatusCode status, string body, string path) => (int)status switch
    {
        401 => CloudProviderException.Unauthorized(),
        403 => ErrorMessage(body) is { } message ? CloudProviderException.CommandFailed(message) : CloudProviderException.Unauthorized(),
        400 when ErrorMessage(body) is { } message => CloudProviderException.CommandFailed(message),
        404 => CloudProviderException.NotFound(path),
        429 => CloudProviderException.RateLimited(),
        440 => CloudProviderException.CommandFailed(Localization.L("This library is encrypted. Unlock it in the Seafile web interface, then try again.")),
        443 or 507 => CloudProviderException.QuotaExceeded(),
        _ => CloudProviderException.ServerError((int)status),
    };

    /// <summary>
    /// Strict percent-encoding for a query value — a name with "&amp;", "=" or "+" must not split
    /// the query — keeping "/" readable.
    /// </summary>
    internal static string QueryEncoded(string value) => Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.Ordinal);

    /// <summary>
    /// The upload-link and file endpoints answer with a URL whose scheme/host/port come from the
    /// server's FILE_SERVER_ROOT setting, which is often misaligned with how the user reaches the
    /// server (https configured while the box only speaks http, or an internal host name). The
    /// typed Server URL is trusted instead: its scheme, host and port replace the returned ones,
    /// keeping the path and query that carry the token Seafile validates.
    /// </summary>
    internal static Uri? RewriteToServer(string rawBody, string serverUrl)
    {
        var raw = rawBody.Trim();
        if (raw.StartsWith('"'))
        {
            try
            {
                raw = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            }
            catch (JsonException)
            {
                raw = raw.Trim('"');
            }
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var original))
        {
            return null;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
        {
            return original;
        }

        var builder = new UriBuilder(original)
        {
            Scheme = server.Scheme,
            Host = server.Host,
            Port = server.IsDefaultPort ? -1 : server.Port,
        };
        return builder.Uri;
    }

    // MARK: - Libraries

    private async Task<IReadOnlyList<SeafileLibrary>> FetchLibrariesAsync(Session session, CancellationToken cancellationToken)
    {
        var body = await SendAsync(session, HttpMethod.Get, "/api2/repos/", "/", cancellationToken).ConfigureAwait(false);
        var libraries = Deserialize<List<SeafileLibrary>>(body);
        var index = BuildNameIndex(libraries);
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _libraries = index;
            }
        }

        return libraries;
    }

    /// <summary>Display name → library. Colliding names get an "(id-prefix)" suffix so each is addressable.</summary>
    internal static Dictionary<string, SeafileLibrary> BuildNameIndex(IReadOnlyList<SeafileLibrary> libraries)
    {
        var counts = libraries.GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var index = new Dictionary<string, SeafileLibrary>(StringComparer.Ordinal);
        foreach (var library in libraries)
        {
            index[DisplayName(library, counts)] = library;
        }

        return index;
    }

    private static string DisplayName(SeafileLibrary library, IReadOnlyDictionary<string, int> counts)
        => counts.GetValueOrDefault(library.Name) > 1 ? $"{library.Name} ({library.Id[..Math.Min(6, library.Id.Length)]})" : library.Name;

    /// <summary>
    /// A panel path as (library, path inside it), or null for "/". Uses the cached index and
    /// refreshes it once on a miss — the library may have been created since.
    /// </summary>
    private async Task<(SeafileLibrary Library, string Inner)?> ResolveAsync(Session session, string path, CancellationToken cancellationToken)
    {
        var components = CloudPath.Components(path);
        if (components.Length == 0)
        {
            return null;
        }

        var key = components[0];
        var inner = "/" + string.Join('/', components.Skip(1));
        SeafileLibrary? library;
        lock (_gate)
        {
            _libraries.TryGetValue(key, out library);
        }

        if (library is null)
        {
            await FetchLibrariesAsync(session, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _libraries.TryGetValue(key, out library);
            }
        }

        return library is null ? throw CloudProviderException.NotFound(path) : (library, inner);
    }

    // MARK: - Listing

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var session = Current;
        var normalized = CloudPath.Normalize(path);
        if (await ResolveAsync(session, normalized, cancellationToken).ConfigureAwait(false) is { } resolved)
        {
            return await ListLibraryPathAsync(session, resolved.Library.Id, resolved.Inner, normalized, cancellationToken).ConfigureAwait(false);
        }

        // The root: libraries as top-level folders. Encrypted libraries are listed like any
        // other; opening one whose password isn't set fails with the 440 explanation.
        var libraries = await FetchLibrariesAsync(session, cancellationToken).ConfigureAwait(false);
        var counts = libraries.GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return libraries.Select(l => LibraryItem(l, DisplayName(l, counts))).ToList();
    }

    private static CloudFileItem LibraryItem(SeafileLibrary library, string name)
        => new("lib:" + library.Id, name, "/" + name, isDirectory: true, library.Size ?? 0, FromUnix(library.Mtime))
        {
            IsHidden = CloudFileItem.IsDotHidden(name),
        };

    private static async Task<IReadOnlyList<CloudFileItem>> ListLibraryPathAsync(Session session, string repoId, string inner, string parentPath, CancellationToken cancellationToken)
    {
        var body = await SendAsync(session, HttpMethod.Get, $"/api2/repos/{repoId}/dir/?p={QueryEncoded(inner)}", parentPath, cancellationToken).ConfigureAwait(false);
        var entries = Deserialize<List<SeafileDirEntry>>(body);
        var items = new List<CloudFileItem>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var isDirectory = entry.Type == "dir";
            var itemPath = CloudPath.Combine(parentPath, entry.Name);
            items.Add(new CloudFileItem(
                $"{(isDirectory ? "d" : "f")}:{repoId}:{itemPath}",
                entry.Name,
                itemPath,
                isDirectory,
                entry.Size ?? 0,
                FromUnix(entry.Mtime),
                isDirectory ? null : entry.Id)
            {
                IsHidden = CloudFileItem.IsDotHidden(entry.Name),
            });
        }

        return items;
    }

    private static DateTimeOffset FromUnix(long? seconds)
        => seconds is { } s and > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : CloudFileItem.UnknownDate;

    /// <summary>Whether <paramref name="inner"/> is a folder, from its parent's listing. False on any failure, as on macOS.</summary>
    private static async Task<bool> IsDirectoryAsync(Session session, string repoId, string inner, CancellationToken cancellationToken)
    {
        try
        {
            var name = CloudPath.Name(inner);
            var siblings = await ListLibraryPathAsync(session, repoId, CloudPath.Parent(inner), "/", cancellationToken).ConfigureAwait(false);
            return siblings.FirstOrDefault(s => s.Name == name)?.IsDirectory == true;
        }
        catch (CloudProviderException)
        {
            return false;
        }
    }

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var session = Current;
        var normalized = CloudPath.Normalize(path);
        if (await ResolveAsync(session, normalized, cancellationToken).ConfigureAwait(false) is not { } resolved)
        {
            return new CloudFileItem("root", string.Empty, "/", isDirectory: true, 0, CloudFileItem.UnknownDate);
        }

        if (resolved.Inner == "/")
        {
            // A library itself (macOS looked it up inside the library and never found it).
            return LibraryItem(resolved.Library, CloudPath.Name(normalized));
        }

        // Probe as a file first (uploads and downloads ask about files); Seafile has no single
        // stat for both, so a folder falls back to its parent's listing.
        using (var request = Request(session, HttpMethod.Get, $"/api2/repos/{resolved.Library.Id}/file/detail/?p={QueryEncoded(resolved.Inner)}"))
        using (var response = await session.Http.SendApiAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                var detail = Deserialize<SeafileFileDetail>(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
                var name = string.IsNullOrEmpty(detail.Name) ? CloudPath.Name(normalized) : detail.Name;
                return new CloudFileItem($"f:{resolved.Library.Id}:{normalized}", name, normalized, isDirectory: false, detail.Size ?? 0, FromUnix(detail.Mtime), detail.Id)
                {
                    IsHidden = CloudFileItem.IsDotHidden(name),
                };
            }
        }

        var parent = CloudPath.Parent(normalized);
        var entries = await ListLibraryPathAsync(session, resolved.Library.Id, CloudPath.Parent(resolved.Inner), parent, cancellationToken).ConfigureAwait(false);
        var target = CloudPath.Name(normalized);
        return entries.FirstOrDefault(e => e.Name == target) ?? throw CloudProviderException.NotFound(path);
    }

    // MARK: - Transfers

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var session = Current;
        if (await ResolveAsync(session, remotePath, cancellationToken).ConfigureAwait(false) is not { } resolved)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        // Step 1: a short-lived signed download URL (a JSON-encoded string).
        var body = await SendAsync(session, HttpMethod.Get, $"/api2/repos/{resolved.Library.Id}/file/?p={QueryEncoded(resolved.Inner)}", remotePath, cancellationToken).ConfigureAwait(false);
        var url = RewriteToServer(body, session.Credentials.ServerUrl) ?? throw CloudProviderException.InvalidResponse();

        // Step 2: stream the bytes to disk. The token in the URL is the authorisation.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await session.Http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, string.Empty, remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    public override async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var session = Current;
        var resolved = await ResolveAsync(session, remotePath, cancellationToken).ConfigureAwait(false);
        if (resolved is not { } target || target.Inner == "/")
        {
            throw CloudProviderException.CommandFailed(Localization.L("Cannot upload directly to the Seafile library root — pick a library first."));
        }

        var parentInner = CloudPath.Parent(target.Inner);
        var fileName = CloudPath.Name(target.Inner);

        // Step 1: a one-shot upload URL.
        var body = await SendAsync(session, HttpMethod.Get, $"/api2/repos/{target.Library.Id}/upload-link/?p={QueryEncoded(parentInner)}", remotePath, cancellationToken).ConfigureAwait(false);
        var url = RewriteToServer(body, session.Credentials.ServerUrl) ?? throw CloudProviderException.InvalidResponse();

        // Step 2: multipart POST. replace=1 overwrites: by the time an upload lands here the
        // conflict resolution has already decided to replace.
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new SeafileUploadContent(localPath, fileName, [("parent_dir", parentInner), ("replace", "1")], onBytes),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", session.Credentials.Token);
        using var response = await session.Http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"Upload failed: HTTP {(int)response.StatusCode}: {error}", LogCategory, LogLevel.Error);
            throw CloudProviderException.SizeLimitError((int)response.StatusCode, localPath) ?? MapError(response.StatusCode, error, remotePath);
        }
    }

    // MARK: - File operations

    public override async Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var session = Current;
        var resolved = await ResolveAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (resolved is not { } target || target.Inner == "/")
        {
            // Deleting a whole library is destructive enough to leave to the web UI rather
            // than expose it through the panel's Delete key.
            throw CloudProviderException.CommandFailed(Localization.L("Cannot delete a Seafile library from FileFluss. Use the Seafile web UI."));
        }

        var isDirectory = await IsDirectoryAsync(session, target.Library.Id, target.Inner, cancellationToken).ConfigureAwait(false);
        var endpoint = isDirectory ? "dir" : "file";
        await SendAsync(session, HttpMethod.Delete, $"/api2/repos/{target.Library.Id}/{endpoint}/?p={QueryEncoded(target.Inner)}", path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Seafile's mkdir renames on a clash ("Photos (1)"), so an existing folder is detected
    /// first — creating a folder that exists must be a quiet no-op.
    /// </summary>
    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var session = Current;
        var components = CloudPath.Components(path);
        if (components.Length <= 1)
        {
            // "/" or a library: libraries are created in the web UI. An existing one is fine.
            if (components.Length == 1)
            {
                try
                {
                    await ResolveAsync(session, path, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
                {
                }
            }

            throw CloudProviderException.CommandFailed(Localization.L("Create a library in the Seafile web UI before adding folders."));
        }

        var target = (await ResolveAsync(session, path, cancellationToken).ConfigureAwait(false))!.Value;
        var name = CloudPath.Name(target.Inner);
        var siblings = await ListLibraryPathAsync(session, target.Library.Id, CloudPath.Parent(target.Inner), CloudPath.Parent(path), cancellationToken).ConfigureAwait(false);
        if (siblings.Any(s => s.Name == name && s.IsDirectory))
        {
            return;
        }

        await SendAsync(
            session,
            HttpMethod.Post,
            $"/api2/repos/{target.Library.Id}/dir/?p={QueryEncoded(target.Inner)}",
            path,
            cancellationToken,
            CloudHttp.FormContent(("operation", "mkdir"))).ConfigureAwait(false);
    }

    public override async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var session = Current;
        var resolved = await ResolveAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (resolved is not { } target || target.Inner == "/")
        {
            throw CloudProviderException.CommandFailed(Localization.L("Rename a Seafile library from its web UI."));
        }

        var isDirectory = await IsDirectoryAsync(session, target.Library.Id, target.Inner, cancellationToken).ConfigureAwait(false);
        await RenameInLibraryAsync(session, target.Library.Id, target.Inner, newName, isDirectory, path, cancellationToken).ConfigureAwait(false);
    }

    private static Task RenameInLibraryAsync(Session session, string repoId, string inner, string newName, bool isDirectory, string path, CancellationToken cancellationToken)
        => SendAsync(
            session,
            HttpMethod.Post,
            $"/api2/repos/{repoId}/{(isDirectory ? "dir" : "file")}/?p={QueryEncoded(inner)}",
            path,
            cancellationToken,
            CloudHttp.FormContent(("operation", "rename"), ("newname", newName)));

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => MoveOrCopyAsync("move", path, newPath, cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => MoveOrCopyAsync("copy", path, newPath, cancellationToken);

    /// <summary>
    /// Server-side move/copy through <c>/api2/repos/{id}/fileops/{move|copy}/</c>, which works for
    /// files and folders, within and across libraries. (Not in the macOS app, which falls back to
    /// download + upload.)
    ///
    /// <para>The API keeps the source name and silently renames on a clash ("a (1).txt"), so the
    /// destination is checked first: an existing target fails rather than being duplicated, and
    /// when the item also needs a new name and its old name is taken there, this gives up
    /// (NotImplemented) so the caller falls back instead of guessing which item to rename.
    /// Names with ":" can't be expressed (the API separates names with ":") and fall back too,
    /// as do libraries themselves and a copy into the same folder.</para>
    /// </summary>
    private async Task MoveOrCopyAsync(string operation, string path, string newPath, CancellationToken cancellationToken)
    {
        var session = Current;
        var destinationParent = CloudPath.Parent(newPath);
        var source = await ResolveAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (source is not { } from || from.Inner == "/" || CloudPath.IsRoot(destinationParent))
        {
            throw CloudProviderException.NotImplemented();
        }

        var to = (await ResolveAsync(session, destinationParent, cancellationToken).ConfigureAwait(false))!.Value;
        var sourceParent = CloudPath.Parent(from.Inner);
        var sourceName = CloudPath.Name(from.Inner);
        var targetName = CloudPath.Name(newPath);
        if (sourceName.Contains(':', StringComparison.Ordinal))
        {
            throw CloudProviderException.NotImplemented();
        }

        var isDirectory = await IsDirectoryAsync(session, from.Library.Id, from.Inner, cancellationToken).ConfigureAwait(false);
        if (from.Library.Id == to.Library.Id && sourceParent == to.Inner)
        {
            if (operation == "move" && sourceName != targetName)
            {
                await RenameInLibraryAsync(session, from.Library.Id, from.Inner, targetName, isDirectory, path, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw CloudProviderException.NotImplemented();
        }

        var existing = await ListLibraryPathAsync(session, to.Library.Id, to.Inner, destinationParent, cancellationToken).ConfigureAwait(false);
        if (existing.Any(e => e.Name == targetName))
        {
            throw CloudProviderException.CommandFailed(Localization.L("An item named “{0}” already exists at the destination.", targetName));
        }

        if (sourceName != targetName && existing.Any(e => e.Name == sourceName))
        {
            throw CloudProviderException.NotImplemented();
        }

        await SendAsync(
            session,
            HttpMethod.Post,
            $"/api2/repos/{from.Library.Id}/fileops/{operation}/?p={QueryEncoded(sourceParent)}",
            path,
            cancellationToken,
            CloudHttp.FormContent(("file_names", sourceName), ("dst_repo", to.Library.Id), ("dst_dir", to.Inner))).ConfigureAwait(false);

        if (sourceName != targetName)
        {
            var placed = CloudPath.Combine(to.Inner, sourceName);
            await RenameInLibraryAsync(session, to.Library.Id, placed, targetName, isDirectory, newPath, cancellationToken).ConfigureAwait(false);
        }
    }

    // MARK: - Quota

    /// <summary>
    /// <c>/api2/account/info/</c> reports <c>usage</c> and <c>total</c> in bytes. Self-hosted servers
    /// often have no quota (total &lt;= 0): used-only then. Null when the call fails, as on macOS.
    /// </summary>
    public override async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
    {
        Session session;
        lock (_gate)
        {
            if (_session is null)
            {
                return null;
            }

            session = _session;
        }

        using var request = Request(session, HttpMethod.Get, "/api2/account/info/");
        using var response = await session.Http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var info = Deserialize<SeafileAccountInfo>(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
        return new CloudStorageQuota(info.Usage ?? 0, info.Total is > 0 ? info.Total : null);
    }

    // MARK: - Share links

    /// <summary>
    /// Creates a public link. An admin can force a password or expiry or switch link sharing off,
    /// so the server's error_msg is surfaced verbatim. "Already exists" is not a failure: Seafile
    /// keeps a link record even after the file at that path was deleted, so a new file at a
    /// previously shared path routinely gets the refusal — the existing link is handed back.
    /// </summary>
    public override async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
    {
        var session = Current;
        if (await ResolveAsync(session, path, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            throw CloudProviderException.CommandFailed(Localization.L("Cannot share the Seafile library list — pick a file or folder inside a library."));
        }

        // Seafile takes whole days. Round up so "tomorrow morning" never lands in the past, and
        // keep at least 1 — 0 means "never expires" to the server.
        int? expireDays = options.Expiry is { } expiry
            ? Math.Max(1, (int)Math.Ceiling((expiry - DateTimeOffset.Now).TotalSeconds / 86_400))
            : null;

        // `permissions` arrived in Seafile 9 and older servers reject unknown fields, so it is
        // sent only when something must be restricted.
        var permissions = options.AllowDownload ? null : new SeafileSharePermissions(false, false, false);
        var payload = new SeafileShareLinkRequest(
            target.Library.Id,
            target.Inner,
            string.IsNullOrEmpty(options.Password) ? null : options.Password,
            expireDays,
            permissions);

        using var request = Request(session, HttpMethod.Post, "/api/v2.1/share-links/", CloudHttp.JsonContent(payload));
        using var response = await session.Http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"share-links failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            var message = ErrorMessage(body);
            if ((message ?? body).Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
                await FirstShareLinkAsync(session, path, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return ShareLinkFrom(existing, Localization.L("This file already had a share link, so that one was returned."));
            }

            throw message is not null ? CloudProviderException.CommandFailed(message) : MapError(response.StatusCode, body, path);
        }

        var created = Deserialize<SeafileShareLink>(body);
        if (!Uri.TryCreate(created.Link, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        // "?dl=1" on the landing page streams the file itself — pointless without download permission.
        var canDownload = created.Permissions?.CanDownload ?? options.AllowDownload;
        return new CloudShareLink(url)
        {
            DirectDownloadUrl = canDownload ? DirectUrl(created.Link!) : null,
            ExpiresAt = ParseShareExpiry(created.ExpireDate),
            HasPassword = !string.IsNullOrEmpty(options.Password),
        };
    }

    /// <summary>
    /// The link this path already has, or null. An unshared path answers <c>200 []</c>, so "not
    /// shared" is an empty list, never an error; the library list at "/" isn't shareable and is
    /// null too.
    /// </summary>
    public override async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => await FirstShareLinkAsync(Current, path, cancellationToken).ConfigureAwait(false) is { } link ? ShareLinkFrom(link) : null;

    /// <summary>
    /// Seafile can't change a live link — the v2.1 API only creates and deletes — so the link is
    /// replaced: a new token and URL, and the note says so because anyone holding the old URL
    /// loses access. The old link goes first because Seafile refuses a second link for a path;
    /// if creation then fails (an admin-enforced password) the file ends up unshared and the
    /// server's message explains why.
    /// </summary>
    public override async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
    {
        var session = Current;
        if (await FirstShareLinkAsync(session, path, cancellationToken).ConfigureAwait(false) is not { Token.Length: > 0 } existing)
        {
            throw CloudProviderException.CommandFailed(Localization.L("This file has no public link to update. Create one first."));
        }

        await DeleteShareLinkAsync(session, existing.Token!, path, cancellationToken).ConfigureAwait(false);
        var fresh = await CreateShareLinkAsync(path, options, cancellationToken).ConfigureAwait(false);
        var replaced = Localization.L("Seafile cannot change a link's settings, so the link was replaced — the previous URL no longer works.");
        return fresh with { Note = string.Join(' ', new[] { fresh.Note, replaced }.Where(n => !string.IsNullOrEmpty(n))) };
    }

    /// <summary>Withdraws the link; not an error when there is none — "not shared" already holds.</summary>
    public override async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        var session = Current;
        if (await FirstShareLinkAsync(session, path, cancellationToken).ConfigureAwait(false) is not { Token.Length: > 0 } existing)
        {
            SupportLogger.Shared.Log("removeShareLink: no public link on that path, nothing to withdraw", LogCategory);
            return;
        }

        await DeleteShareLinkAsync(session, existing.Token!, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/v2.1/share-links/?repo_id=…&amp;path=…</c> → the first link (the one the web UI
    /// shows when a configuration allows several), or null.
    /// </summary>
    private async Task<SeafileShareLink?> FirstShareLinkAsync(Session session, string path, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(session, path, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return null;
        }

        var query = $"repo_id={QueryEncoded(target.Library.Id)}&path={QueryEncoded(target.Inner)}";
        using var request = Request(session, HttpMethod.Get, "/api/v2.1/share-links/?" + query);
        using var response = await session.Http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"share-links lookup failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            throw ErrorMessage(body) is { } message ? CloudProviderException.CommandFailed(message) : MapError(response.StatusCode, body, path);
        }

        return Deserialize<List<SeafileShareLink>>(body).FirstOrDefault();
    }

    /// <summary><c>DELETE /api/v2.1/share-links/&lt;token&gt;/</c>. A 404 means it is already gone — the outcome wanted.</summary>
    private static async Task DeleteShareLinkAsync(Session session, string token, string path, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Delete, $"/api/v2.1/share-links/{QueryEncoded(token)}/");
        using var response = await session.Http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound || response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"share-link delete failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
        throw ErrorMessage(body) is { } message ? CloudProviderException.CommandFailed(message) : MapError(response.StatusCode, body, path);
    }

    private static CloudShareLink ShareLinkFrom(SeafileShareLink link, string? note = null)
    {
        if (!Uri.TryCreate(link.Link, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(url)
        {
            DirectDownloadUrl = link.Permissions?.CanDownload ?? true ? DirectUrl(link.Link!) : null,
            ExpiresAt = ParseShareExpiry(link.ExpireDate),
            // Not every release reports the password state on a listed link. Where it's absent
            // the link is reported as open — the safer direction: it never claims protection
            // the link may not have.
            HasPassword = link.IsPasswordSet ?? !string.IsNullOrEmpty(link.Password),
            Note = note,
        };
    }

    private static Uri DirectUrl(string link) => new(link.Contains('?', StringComparison.Ordinal) ? link + "&dl=1" : link + "?dl=1");

    /// <summary>
    /// <c>expire_date</c> is ISO 8601 — with an offset on current releases, without one on some
    /// older builds (read as the server's local time, the best guess without a zone) — and ""
    /// or null when the link never expires.
    /// </summary>
    internal static DateTimeOffset? ParseShareExpiry(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _session?.Http.Dispose();
                _session = null;
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>Credentials and the client configured for them (self-signed trust is per host), swapped together.</summary>
    private sealed record Session(SeafileCredentials Credentials, HttpClient Http);
}
