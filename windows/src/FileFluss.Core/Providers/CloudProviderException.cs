// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Formatting;

namespace FileFluss.Core.Providers;

/// <summary>The macOS <c>CloudProviderError</c> cases.</summary>
public enum CloudErrorKind
{
    NotAuthenticated,
    NotImplemented,
    NetworkError,
    Unauthorized,

    /// <summary>The server wants a one-time code as well; add-account flows re-prompt for it.</summary>
    TwoFactorRequired,
    NotFound,
    QuotaExceeded,
    RateLimited,
    ServerError,

    /// <summary>A non-HTTP transport (SFTP, FTP) failing with a human-readable message.</summary>
    CommandFailed,
    InvalidResponse,
    InvalidCredentials,
    FileTooLarge,
    Cancelled,
}

/// <summary>
/// The one exception type providers throw for expected failures. The message is already
/// localized and fit to show the user; <see cref="Kind"/> lets callers react (re-auth banner
/// on <see cref="CloudErrorKind.Unauthorized"/>, fallback on
/// <see cref="CloudErrorKind.NotImplemented"/>, 2FA prompt on
/// <see cref="CloudErrorKind.TwoFactorRequired"/>).
/// </summary>
public sealed class CloudProviderException : Exception
{
    private CloudProviderException(CloudErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public CloudErrorKind Kind { get; }

    /// <summary>HTTP status for <see cref="CloudErrorKind.ServerError"/>.</summary>
    public int? StatusCode { get; private init; }

    /// <summary>The item a <see cref="CloudErrorKind.NotFound"/> refers to.</summary>
    public string? ItemPath { get; private init; }

    public long? FileBytes { get; private init; }

    public long? ProviderLimitBytes { get; private init; }

    /// <summary>True for the two "sign in again" cases the panel turns into a re-auth banner.</summary>
    public bool NeedsReauthentication => Kind is CloudErrorKind.Unauthorized or CloudErrorKind.NotAuthenticated;

    public static CloudProviderException NotAuthenticated()
        => new(CloudErrorKind.NotAuthenticated, Localization.L("Not authenticated. Please sign in."));

    public static CloudProviderException NotImplemented()
        => new(CloudErrorKind.NotImplemented, Localization.L("This feature is not yet implemented."));

    public static CloudProviderException Network(Exception inner)
        => new(CloudErrorKind.NetworkError, Localization.L("Network error: {0}", inner.Message), inner);

    public static CloudProviderException Unauthorized()
        => new(CloudErrorKind.Unauthorized, Localization.L("Authorization expired. Please sign in again."));

    public static CloudProviderException TwoFactorRequired()
        => new(CloudErrorKind.TwoFactorRequired, Localization.L("Two-factor authentication required. Enter your 6-digit code and try again."));

    public static CloudProviderException NotFound(string path)
        => new(CloudErrorKind.NotFound, Localization.L("Item not found: {0}", path)) { ItemPath = path };

    public static CloudProviderException QuotaExceeded()
        => new(CloudErrorKind.QuotaExceeded, Localization.L("Storage quota exceeded."));

    public static CloudProviderException RateLimited()
        => new(CloudErrorKind.RateLimited, Localization.L("Rate limited. Please try again later."));

    /// <summary>
    /// A failed OAuth token refresh. Only the server rejecting the grant (400 invalid_grant,
    /// 401, 403) means "sign in again"; a 429 or a 5xx during an outage is passing, and turning
    /// it into the re-auth banner would send the user through a sign-in that fixes nothing.
    /// </summary>
    public static CloudProviderException TokenRefreshFailed(int statusCode) => statusCode switch
    {
        400 or 401 or 403 => NotAuthenticated(),
        429 => RateLimited(),
        _ => ServerError(statusCode),
    };

    public static CloudProviderException ServerError(int statusCode)
        => new(CloudErrorKind.ServerError, Localization.L("Server error (HTTP {0}).", statusCode)) { StatusCode = statusCode };

    /// <summary>A failure with a message of its own — the server's explanation, or an SFTP status line.</summary>
    public static CloudProviderException CommandFailed(string message, Exception? inner = null)
        => new(CloudErrorKind.CommandFailed, message, inner);

    public static CloudProviderException InvalidResponse()
        => new(CloudErrorKind.InvalidResponse, Localization.L("Invalid response from server."));

    public static CloudProviderException InvalidCredentials()
        => new(CloudErrorKind.InvalidCredentials, Localization.L("Invalid email or password."));

    public static CloudProviderException Cancelled()
        => new(CloudErrorKind.Cancelled, Localization.L("Cancelled"));

    public static CloudProviderException FileTooLarge(long fileBytes, long? providerLimitBytes)
    {
        var size = ByteFormatter.Format(fileBytes);
        var message = providerLimitBytes is { } limit
            ? Localization.L("File is too large to upload ({0}). This provider's limit is {1}.", size, ByteFormatter.Format(limit))
            : Localization.L("File is too large to upload ({0}). The provider rejected it. Try splitting the file or using a different cloud.", size);
        return new(CloudErrorKind.FileTooLarge, message) { FileBytes = fileBytes, ProviderLimitBytes = providerLimitBytes };
    }

    /// <summary>
    /// Pre-flight: throws <see cref="CloudErrorKind.FileTooLarge"/> when the local file exceeds
    /// the provider's documented per-file limit, saving a wasted upload.
    /// </summary>
    public static void EnforceUploadSizeLimit(string localFile, long? limitBytes)
    {
        if (limitBytes is not { } limit)
        {
            return;
        }

        var size = new FileInfo(localFile).Length;
        if (size > limit)
        {
            throw FileTooLarge(size, limit);
        }
    }

    /// <summary>
    /// Maps an upload response status that usually means "too big" (413 always; 422 only for a
    /// suspiciously large file, since WebDAV servers reuse it) to <see cref="CloudErrorKind.FileTooLarge"/>.
    /// Returns null when the status is unrelated to size.
    /// </summary>
    public static CloudProviderException? SizeLimitError(int status, string localFile)
    {
        long size = 0;
        try
        {
            size = new FileInfo(localFile).Length;
        }
        catch (IOException)
        {
        }

        return status switch
        {
            413 => FileTooLarge(size, null),
            422 when size > 2_000_000_000 => FileTooLarge(size, null),
            _ => null,
        };
    }
}
