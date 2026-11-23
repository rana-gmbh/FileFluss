// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers;

/// <summary>
/// A connected (or connectable) cloud account: the macOS <c>CloudProvider</c> protocol plus
/// the sign-in plumbing each macOS provider repeated by hand.
///
/// <para><b>Lifecycle.</b> The registry constructs a provider with the account's id and the
/// credential store. The constructor restores stored credentials (no network);
/// <see cref="IsAuthenticated"/> then says whether there were any. A new account is created
/// the same way with a fresh id, after which <see cref="ConnectAsync"/> signs in and persists
/// credentials under <see cref="CredentialKey"/>. Editing an account calls
/// <see cref="ConnectAsync"/> again on a provider for the same id, which overwrites them.</para>
///
/// <para><b>Concurrency.</b> Panels, transfers and indexing call one provider instance
/// concurrently. Implementations must be thread-safe — in particular token refresh must be
/// single-flight (concurrent callers await the same refresh), because providers such as
/// Dropbox rotate refresh tokens and a parallel refresh invalidates the other.</para>
/// </summary>
public abstract class CloudProvider : FileProvider, IDisposable
{
    protected CloudProvider(Guid accountId, ICredentialStore credentials)
    {
        AccountId = accountId;
        Credentials = credentials;
    }

    public Guid AccountId { get; }

    public abstract CloudProviderType ProviderType { get; }

    protected ICredentialStore Credentials { get; }

    /// <summary>The macOS keychain key prefix, e.g. "dropbox" → "dropbox.&lt;uuid&gt;".</summary>
    protected abstract string CredentialKeyPrefix { get; }

    protected string CredentialKey => $"{CredentialKeyPrefix}.{AccountId}";

    /// <summary>Stored credentials were found (or <see cref="ConnectAsync"/> succeeded).</summary>
    public abstract bool IsAuthenticated { get; }

    // MARK: - Connecting

    /// <summary>The Add/Edit Account form.</summary>
    public abstract ConnectSpec ConnectSpec { get; }

    /// <summary>
    /// Signs in with what the user entered, persists the credentials under
    /// <see cref="CredentialKey"/>, and returns the label the account is shown with. Throws
    /// <see cref="CloudProviderException"/> with a message fit for the form on failure, and
    /// must leave previously stored credentials untouched when it fails.
    /// </summary>
    public abstract Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Non-secret values to prefill the Edit Account form with (server URL, user name). Never passwords or tokens.</summary>
    public virtual IReadOnlyDictionary<string, string> GetEditableConnectValues() => new Dictionary<string, string>();

    /// <summary>Forgets the stored credentials (and a pinned certificate).</summary>
    public virtual Task DisconnectAsync()
    {
        Credentials.Delete(CredentialKey);
        Credentials.Delete(CertificatePinKey);
        return Task.CompletedTask;
    }

    // MARK: - Self-signed certificates (trust on first use)

    /// <summary>
    /// Where the account's pinned server certificate is kept: beside its credentials in the same
    /// store, but under a key of its own, so token refreshes that rewrite the credentials can
    /// never drop it and providers without the option never carry it.
    /// </summary>
    protected string CertificatePinKey => $"{CredentialKey}.certificate";

    /// <summary>The fingerprint pinned for <paramref name="host"/>, or null (none, or pinned for another host).</summary>
    protected string? StoredCertificatePin(string host)
        => Credentials.Load<CertificatePinRecord>(CertificatePinKey) is { } record &&
           string.Equals(record.Host, host, StringComparison.OrdinalIgnoreCase)
            ? record.Fingerprint
            : null;

    /// <summary>Remembers <paramref name="fingerprint"/> as the certificate to trust for <paramref name="host"/>; null forgets it.</summary>
    protected void SaveCertificatePin(string host, string? fingerprint)
    {
        if (fingerprint is null)
        {
            Credentials.Delete(CertificatePinKey);
        }
        else
        {
            Credentials.Save(CertificatePinKey, new CertificatePinRecord(host, fingerprint));
        }
    }

    /// <summary>
    /// The trust a stored account's client uses: null without the option; otherwise the pinned
    /// certificate, and — for an account pinned nothing yet (set up before pinning existed, so
    /// it already trusted whatever its server showed) — the first one seen, remembered at once.
    /// </summary>
    protected Net.SelfSignedTrust? SelfSignedTrustFor(string? host, bool allowSelfSigned)
        => allowSelfSigned && !string.IsNullOrEmpty(host)
            ? new Net.SelfSignedTrust(host, StoredCertificatePin(host), fingerprint => SaveCertificatePin(host, fingerprint))
            : null;

    /// <summary>
    /// Runs a sign-in <paramref name="attempt"/> with the certificate trust the form asked for.
    /// The pin stored for the same host carries over. A certificate that changed since is shown
    /// with both fingerprints and accepted only on an explicit yes (then the attempt runs once
    /// more, trusting exactly the certificate the user saw), the way a changed SSH host key is.
    /// The pin is stored only once the attempt succeeded, so a failed sign-in changes nothing;
    /// turning the option off forgets it.
    /// </summary>
    protected async Task<T> ConnectTrustingAsync<T>(
        string? host,
        bool allowSelfSigned,
        IConnectPrompt prompt,
        Func<Net.SelfSignedTrust?, Task<T>> attempt,
        CancellationToken cancellationToken)
    {
        if (!allowSelfSigned || string.IsNullOrEmpty(host))
        {
            var plain = await attempt(null).ConfigureAwait(false);
            Credentials.Delete(CertificatePinKey);
            return plain;
        }

        var trust = new Net.SelfSignedTrust(host, StoredCertificatePin(host));
        T result;
        try
        {
            result = await attempt(trust).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && CloudProviderException.FindCertificateChange(e) is { } changed)
        {
            var confirmed = await prompt.ConfirmAsync(
                Localization.L("Certificate changed"),
                Localization.L(
                    "The certificate of {0} has changed since you last connected. This happens when the server got a new certificate — or when someone intercepts the connection.\n\nPreviously: {1}\nNow: {2}\n\nTrust the new certificate?",
                    host,
                    changed.Previous,
                    changed.Presented),
                cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                throw CloudProviderException.CertificateChanged(changed);
            }

            trust = new Net.SelfSignedTrust(host, changed.Presented);
            result = await attempt(trust).ConfigureAwait(false);
        }

        // Null when the server's certificate validated normally: nothing to pin, and a pin
        // kept from before stays (trust started from it).
        if (trust.Pin is { } pin)
        {
            SaveCertificatePin(host, pin);
        }

        return result;
    }

    private sealed record CertificatePinRecord(string Host, string Fingerprint);

    /// <summary>
    /// Re-reads the account's user-facing name after launch (Dropbox heals a stuck "Unknown").
    /// Null when nothing changed or the lookup failed — a failure must never replace a good name.
    /// </summary>
    public virtual Task<string?> RefreshAccountLabelAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    // MARK: - Limits and quota

    /// <summary>Documented per-file upload cap, checked before uploading; null when there is none.</summary>
    public virtual long? MaxUploadFileSize => null;

    /// <summary>Account-wide usage; null when the provider has no quota API. Do not cache here.</summary>
    public virtual Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Thumbnails

    /// <summary>
    /// A small preview of a file made by the service itself, as image bytes (JPEG or PNG)
    /// whose longer edge is about <paramref name="maxPixelSize"/> — the macOS
    /// <c>thumbnail(for:maxPixelSize:)</c>. The gallery shows it instead of the type icon.
    ///
    /// <para>Null means the service has no preview for this file, or no preview API at all,
    /// which is the default. The gallery then downloads small images and PDFs and lets the
    /// shell draw them, so a provider implements this only to save that download (and to give
    /// videos a preview). Per-file refusals (unsupported type, still being made) return null;
    /// network failures throw <see cref="HttpRequestException"/> or
    /// <see cref="OperationCanceledException"/> so the caller can try again later.</para>
    /// </summary>
    public virtual Task<byte[]?> ThumbnailAsync(CloudFileItem item, int maxPixelSize, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    public override async Task<long?> AvailableSpaceAsync(string path, CancellationToken cancellationToken = default)
        => (await StorageQuotaAsync(cancellationToken).ConfigureAwait(false))?.FreeBytes;

    // MARK: - Share links

    public virtual ShareLinkCapabilities ShareLinkCapabilities => ShareLinkCapabilities.Unsupported;

    /// <summary>Creates a public link. Surface the server's own message on failure (disabled sharing, paid-plan options).</summary>
    public virtual Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>The link the file already has, or null. Only when <see cref="ShareLinkCapabilities.CanQueryExisting"/>.</summary>
    public virtual Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>Changes password/expiry/download permission in place, keeping the URL where possible.</summary>
    public virtual Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>Withdraws the public link.</summary>
    public virtual Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
