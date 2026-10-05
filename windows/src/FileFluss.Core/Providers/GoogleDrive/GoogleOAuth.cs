// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GoogleDrive;

/// <summary>An access/refresh token pair and when the access token expires.</summary>
internal sealed record GoogleToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Refresh a minute early so a token never runs out in the middle of a request.</summary>
    public bool IsExpiring(DateTimeOffset now) => now >= ExpiresAt.AddSeconds(-60);
}

/// <summary>What a completed browser sign-in produced.</summary>
internal sealed record GoogleSignIn(GoogleToken Token, string UserEmail, string DisplayName);

/// <summary>
/// Runs one loopback OAuth round trip: open the browser at the authorize URL built from the
/// redirect URI, wait for the callback. <see cref="OAuthLoopback.AuthenticateAsync"/> in the
/// app; a scripted fake in tests.
/// </summary>
internal delegate Task<OAuthCallback> OAuthRunner(Func<string, Uri> authorizeUrl, CancellationToken cancellationToken);

/// <summary>
/// One Google Cloud OAuth client. FileFluss has two, deliberately kept apart:
/// <list type="bullet">
/// <item><b>Project A</b> (<see cref="FullDrive"/>) — the full <c>drive</c> scope. A
/// restricted scope: it needed a security audit and is capped at 100 users, which it has
/// reached; existing accounts keep working.</item>
/// <item><b>Project B</b> (<see cref="SelectedFolders"/>) — the non-sensitive
/// <c>drive.file</c> scope, driven by the Google Picker. No audit, no user cap.</item>
/// </list>
/// Both are Google "Desktop application" clients — the macOS clients, not the iOS ones.
/// Desktop clients only accept loopback redirects (<c>http://localhost:&lt;port&gt;</c>) and
/// ship with a client secret that Google does not treat as confidential; PKCE protects the
/// code exchange.
/// </summary>
internal sealed class GoogleOAuthClient
{
    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";

    /// <summary>Project A — "Google Drive" (full access).</summary>
    public static readonly GoogleOAuthClient FullDrive = new(
        "682536313816-j07jrk2kbff3sljb16vfal5soqs8vte9.apps.googleusercontent.com",
        "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",
        "https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/userinfo.email",
        "googleDrive");

    /// <summary>Project B — "Google Drive (Selected Folders)".</summary>
    public static readonly GoogleOAuthClient SelectedFolders = new(
        "368567371288-hvv93hksp81tblrrk4ra9vi5svi71dv2.apps.googleusercontent.com",
        "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",
        "https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/userinfo.email openid",
        "googleDrivePicker");

    private GoogleOAuthClient(string clientId, string clientSecret, string scopes, string logCategory)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        Scopes = scopes;
        LogCategory = logCategory;
    }

    public string ClientId { get; }

    public string ClientSecret { get; }

    public string Scopes { get; }

    public string LogCategory { get; }

    public Uri BuildAuthorizeUri(string redirectUri, string codeChallenge, string state)
        => new(AuthorizeEndpoint + "?" + GoogleDriveApi.QueryString(
        [
            ("client_id", ClientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", Scopes),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            // offline + consent: Google only issues a refresh token on a consent screen.
            ("access_type", "offline"),
            ("prompt", "consent"),
            ("state", state),
        ]));

    /// <summary>Browser sign-in with PKCE and state, then the code exchange and the user's name.</summary>
    public async Task<GoogleSignIn> SignInAsync(HttpClient http, OAuthRunner runner, CancellationToken cancellationToken)
    {
        var verifier = Pkce.CreateVerifier();
        var challenge = Pkce.Challenge(verifier);
        var state = Pkce.CreateState();

        var callback = await runner(redirect => BuildAuthorizeUri(redirect, challenge, state), cancellationToken).ConfigureAwait(false);

        // Rejects a callback whose state isn't ours (a code the user didn't ask for) and
        // the browser's error parameter, as macOS does.
        var code = callback.RequireCode(state);
        var token = await ExchangeCodeAsync(http, code, verifier, callback.RedirectUri, cancellationToken).ConfigureAwait(false);
        var (email, name) = await FetchUserInfoAsync(http, token.AccessToken, cancellationToken).ConfigureAwait(false);
        return new GoogleSignIn(token, email, string.IsNullOrEmpty(name) ? email : name);
    }

    private async Task<GoogleToken> ExchangeCodeAsync(HttpClient http, string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = CloudHttp.FormContent(
                ("code", code),
                ("client_id", ClientId),
                ("redirect_uri", redirectUri),
                ("grant_type", "authorization_code"),
                ("code_verifier", verifier),
                ("client_secret", ClientSecret)),
        };

        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // An error body carries no token, so it is safe (and useful) to log.
            var snippet = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"Token exchange failed: HTTP {(int)response.StatusCode}: {snippet}", LogCategory, LogLevel.Error);
            throw CloudProviderException.InvalidCredentials();
        }

        // A successful body IS the access and refresh token: log the status only.
        SupportLogger.Shared.Log($"Token exchange returned HTTP {(int)response.StatusCode}", LogCategory);
        var body = await response.ReadJsonAsync<GoogleTokenResponse>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body.AccessToken))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new GoogleToken(body.AccessToken, body.RefreshToken ?? string.Empty, DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn));
    }

    /// <summary>
    /// Trades the refresh token for a new access token. Google keeps the refresh token unless
    /// the response carries a new one. Any non-2xx means the grant is gone: sign in again.
    /// </summary>
    public async Task<GoogleToken> RefreshAsync(HttpClient http, GoogleToken current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw CloudProviderException.NotAuthenticated();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = CloudHttp.FormContent(
                ("client_id", ClientId),
                ("refresh_token", current.RefreshToken),
                ("grant_type", "refresh_token"),
                ("client_secret", ClientSecret)),
        };

        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"Token refresh failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            throw CloudProviderException.NotAuthenticated();
        }

        var body = await response.ReadJsonAsync<GoogleTokenResponse>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body.AccessToken))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new GoogleToken(
            body.AccessToken,
            string.IsNullOrEmpty(body.RefreshToken) ? current.RefreshToken : body.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn));
    }

    /// <summary>The account's e-mail and name. A failed lookup yields "Unknown" rather than failing the sign-in.</summary>
    private static async Task<(string Email, string? Name)> FetchUserInfoAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ("Unknown", null);
        }

        var info = await response.ReadJsonAsync<GoogleUserInfo>(cancellationToken).ConfigureAwait(false);
        return (string.IsNullOrEmpty(info.Email) ? "Unknown" : info.Email, info.Name);
    }

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }

    private sealed class GoogleUserInfo
    {
        public string? Email { get; init; }

        public string? Name { get; init; }
    }
}

/// <summary>
/// The signed-in identity plus its token, with single-flight refresh. Concurrent callers
/// must await one refresh rather than firing parallel POSTs with the same refresh token —
/// Google may invalidate one of them. A refreshed token is handed to
/// <c>onRefreshed</c> at once so it is persisted before anyone uses it.
/// </summary>
internal sealed class GoogleTokenSession
{
    private readonly HttpClient _http;
    private readonly GoogleOAuthClient _client;
    private readonly Action<GoogleTokenSession>? _onRefreshed;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private GoogleToken _token;

    public GoogleTokenSession(HttpClient http, GoogleOAuthClient client, GoogleToken token, string userEmail, string displayName, Action<GoogleTokenSession>? onRefreshed)
    {
        _http = http;
        _client = client;
        _token = token;
        UserEmail = userEmail;
        DisplayName = displayName;
        _onRefreshed = onRefreshed;
    }

    public string UserEmail { get; }

    public string DisplayName { get; }

    public GoogleToken Token => Volatile.Read(ref _token);

    /// <summary>A usable access token, refreshed first when it is about to expire.</summary>
    public Task<string> AccessTokenAsync(CancellationToken cancellationToken) => ObtainAsync(rejected: null, cancellationToken);

    /// <summary>
    /// Refreshes after the server answered 401 to <paramref name="rejectedAccessToken"/> —
    /// Google can revoke an access token before its stated expiry. When another caller has
    /// already replaced that token, its result is reused instead of refreshing again.
    /// </summary>
    public Task<string> RefreshAfterRejectionAsync(string rejectedAccessToken, CancellationToken cancellationToken)
        => ObtainAsync(rejectedAccessToken, cancellationToken);

    private async Task<string> ObtainAsync(string? rejected, CancellationToken cancellationToken)
    {
        var current = Token;
        if (rejected is null && !current.IsExpiring(DateTimeOffset.UtcNow))
        {
            return current.AccessToken;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Whoever held the gate before us may already have refreshed.
            current = Token;
            var stillNeeded = rejected is null
                ? current.IsExpiring(DateTimeOffset.UtcNow)
                : string.Equals(current.AccessToken, rejected, StringComparison.Ordinal);
            if (!stillNeeded)
            {
                return current.AccessToken;
            }

            var refreshed = await _client.RefreshAsync(_http, current, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _token, refreshed);
            _onRefreshed?.Invoke(this);
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
