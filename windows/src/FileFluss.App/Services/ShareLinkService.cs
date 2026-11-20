// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Services;

/// <summary>
/// Public links for cloud files — the sharing half of the macOS
/// <c>CloudFileManagerViewModel</c>, used by both the one-click context-menu actions
/// ("Copy Share Link", "Stop Sharing") and the options dialog ("Share Link…", "Change Sharing…").
/// Creates, changes and removes links, checks a new link the way a stranger would, puts it on
/// the clipboard, and remembers which files are shared so the menu can say so.
/// </summary>
public sealed class ShareLinkService
{
    private static ShareLinkService? _shared;

    private readonly AppServices _services;
    private readonly Dictionary<(Guid Account, string Path), CloudShareLink?> _states = [];

    public ShareLinkService(AppServices services) => _services = services;

    public static ShareLinkService Shared => _shared ??= new ShareLinkService(AppServices.Current);

    // MARK: - Remembered state

    /// <summary>
    /// True or false once looked up, null while unknown. Only providers that can read their
    /// links back ever answer — the others have no "existing link" to speak of.
    /// </summary>
    public bool? IsShared(CloudProvider provider, string path)
    {
        if (!provider.ShareLinkCapabilities.CanQueryExisting)
        {
            return null;
        }

        return _states.TryGetValue((provider.AccountId, path), out var link) ? link is not null : null;
    }

    /// <summary>
    /// Looks up whether <paramref name="path"/> is shared (macOS <c>refreshShareState</c>).
    /// A failed lookup counts as "not shared": either way the menu offers to create a link.
    /// </summary>
    public async Task<bool> RefreshStateAsync(CloudProvider provider, string path)
    {
        if (!provider.ShareLinkCapabilities.CanQueryExisting)
        {
            return false;
        }

        CloudShareLink? link;
        try
        {
            link = await provider.ExistingShareLinkAsync(path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            link = null;
        }

        Remember(provider, path, link);
        return link is not null;
    }

    /// <summary>The link a file has now, straight from the provider; failures propagate.</summary>
    public async Task<CloudShareLink?> ExistingAsync(CloudProvider provider, string path)
    {
        var link = await provider.ExistingShareLinkAsync(path);
        Remember(provider, path, link);
        return link;
    }

    private void Remember(CloudProvider provider, string path, CloudShareLink? link)
    {
        // S3-style accounts have no stored link: recording one would make the menu offer
        // actions for a link that can't be looked up.
        if (provider.ShareLinkCapabilities.CanQueryExisting)
        {
            _states[(provider.AccountId, path)] = link;
        }
    }

    // MARK: - Create, change, remove

    /// <summary>
    /// Creates a public link, checks it and puts it on the clipboard (macOS
    /// <c>createShareLink</c>). With <paramref name="reuseExisting"/>, a file that is already
    /// shared hands back its existing link rather than failing — that is what "Copy Share Link"
    /// asks for. Applying settings the user just chose must never do that.
    /// </summary>
    public async Task<CloudShareLink> CreateAsync(CloudProvider provider, string path, ShareLinkOptions options, bool reuseExisting)
    {
        try
        {
            CloudShareLink? link = null;
            if (reuseExisting && provider.ShareLinkCapabilities.CanQueryExisting)
            {
                try
                {
                    link = await provider.ExistingShareLinkAsync(path);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not shared, or the lookup failed — either way, creating is the next step.
                }
            }

            link ??= await provider.CreateShareLinkAsync(path, options);
            Remember(provider, path, link);
            NoteAccepted(provider, options);
            link = await VerifyAsync(provider, link);
            CopyToClipboard(link);
            return link;
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsSignInError(e))
        {
            NoteRejected(provider, options, e.Message);
            throw;
        }
    }

    /// <summary>Applies new settings to the link a file already has (macOS <c>updateShareLink</c>).</summary>
    public async Task<CloudShareLink> UpdateAsync(CloudProvider provider, string path, ShareLinkOptions options)
    {
        try
        {
            var link = await provider.UpdateShareLinkAsync(path, options);
            Remember(provider, path, link);
            NoteAccepted(provider, options);
            link = await VerifyAsync(provider, link);
            CopyToClipboard(link);
            return link;
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsSignInError(e))
        {
            NoteRejected(provider, options, e.Message);
            throw;
        }
    }

    /// <summary>
    /// Re-checks and copies the link a file already has. Null when someone removed it
    /// elsewhere in the meantime (macOS <c>copyExistingShareLink</c>).
    /// </summary>
    public async Task<CloudShareLink?> CopyExistingAsync(CloudProvider provider, string path)
    {
        var link = await ExistingAsync(provider, path);
        if (link is null)
        {
            return null;
        }

        link = await VerifyAsync(provider, link);
        CopyToClipboard(link);
        return link;
    }

    /// <summary>Withdraws the public link (macOS <c>stopSharing</c>).</summary>
    public async Task RemoveAsync(CloudProvider provider, string path)
    {
        await provider.RemoveShareLinkAsync(path);
        Remember(provider, path, null);
        SupportLogger.Shared.Log("Share link removed", "Sharing");
    }

    public static bool IsSignInError(Exception e) => e is CloudProviderException { Kind: CloudErrorKind.NotAuthenticated };

    // MARK: - Clipboard

    /// <summary>The address handed out: the direct download where preferred and offered, else the share page.</summary>
    public Uri UrlFor(CloudShareLink link) => _services.Settings.ShareLinkPreferDirectDownload ? link.PreferredUrl : link.Url;

    public void CopyToClipboard(CloudShareLink link)
    {
        try
        {
            Clipboard.SetText(UrlFor(link).ToString());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app holds the clipboard open; the link is still shown.
        }
    }

    // MARK: - Messages

    /// <summary>Adds the missing "why" to a bare refusal like Box's "Forbidden" (macOS <c>shareFailureMessage</c>).</summary>
    public static string FailureMessage(Exception e, ShareLinkOptions options)
    {
        var message = e.Message;
        if (ShareLinkRefusal.RefusedOptionsKey(options, message) is not { } what)
        {
            return message;
        }

        return message + "\n\n" + L("This account refused {0} for the link. Most providers only offer these on paid plans. Sharing without them will still work, and these options are now hidden for this account.", L(what));
    }

    // MARK: - Per-account memory

    public bool PasswordRejected(Guid account) => _services.Settings.GetFlag($"shareOptionRejected.password.{account}");

    public bool ExpiryRejected(Guid account) => _services.Settings.GetFlag($"shareOptionRejected.expiry.{account}");

    /// <summary>Links from this account have needed a sign-in when checked.</summary>
    public bool LinksNotPublic(Guid account) => _services.Settings.GetFlag($"shareLinksNotPublic.{account}");

    /// <summary>An option this account refused before now works — plans get upgraded.</summary>
    private void NoteAccepted(CloudProvider provider, ShareLinkOptions options)
    {
        if (!string.IsNullOrEmpty(options.Password))
        {
            _services.Settings.SetFlag($"shareOptionRejected.password.{provider.AccountId}", false);
        }

        if (options.Expiry is not null)
        {
            _services.Settings.SetFlag($"shareOptionRejected.expiry.{provider.AccountId}", false);
        }
    }

    /// <summary>A plan or policy refusal of a paid-only option means: stop offering it for this account.</summary>
    private void NoteRejected(CloudProvider provider, ShareLinkOptions options, string message)
    {
        if (!ShareLinkRefusal.IsPlanRefusal(message))
        {
            return;
        }

        if (!string.IsNullOrEmpty(options.Password))
        {
            _services.Settings.SetFlag($"shareOptionRejected.password.{provider.AccountId}", true);
        }

        if (options.Expiry is not null)
        {
            _services.Settings.SetFlag($"shareOptionRejected.expiry.{provider.AccountId}", true);
        }
    }

    // MARK: - Verification

    /// <summary>Checks the link as a stranger; downgrades a refused direct link to the share page.</summary>
    private async Task<CloudShareLink> VerifyAsync(CloudProvider provider, CloudShareLink link)
    {
        var accountKey = provider.AccountId;
        if (link.DirectDownloadUrl is { } direct && direct != link.Url)
        {
            var blockedKey = $"shareLinkDirectBlocked.{accountKey}";
            if (_services.Settings.GetFlag(blockedKey))
            {
                return link with { DirectDownloadUrl = null, Note = Join(link.Note, L("This account can't serve direct download links, so the share page link was copied instead.")) };
            }

            var directCheck = await PublicUrlCheck.IsReachableAsync(direct);
            if (directCheck.CouldNotCheck || directCheck.Ok)
            {
                return link;
            }

            _services.Settings.SetFlag(blockedKey, true);
            return link with { DirectDownloadUrl = null, Note = Join(link.Note, L("This account can't serve direct download links, so the share page link was copied instead.")) };
        }

        var result = await PublicUrlCheck.IsReachableAsync(link.Url);
        var notPublicKey = $"shareLinksNotPublic.{accountKey}";
        if (result.Ok)
        {
            _services.Settings.SetFlag(notPublicKey, false);
            return link;
        }

        if (result.CouldNotCheck)
        {
            return link with { Note = Join(link.Note, L("The link couldn't be checked from here: {0}", result.Detail ?? L("the server didn't answer."))) };
        }

        _services.Settings.SetFlag(notPublicKey, true);
        return link with { IsPublic = false, Note = Join(link.Note, L("Checked just now: {0}", result.Detail ?? L("the link is not reachable without signing in."))) };
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;
}
