// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using Method = FileFluss.Core.Models.ReconnectMethod;

namespace FileFluss.Core.Models;

/// <summary>
/// How an account whose sign-in stopped working gets it back — the macOS
/// <c>SyncViewModel.ReconnectMethod</c>. Decides what the panel's Reconnect button and the
/// sidebar's menu item do.
/// </summary>
public enum ReconnectMethod
{
    /// <summary>The provider's own sign-in page, in the browser — nothing to type in FileFluss.</summary>
    Browser,

    /// <summary>
    /// The account's form, filled in with whatever is still saved: a server, a user name, a
    /// token field, a fresh QR code.
    /// </summary>
    Credentials,

    /// <summary>The system's own iCloud sign-in — iCloud for Windows here, System Settings on the Mac.</summary>
    SystemSettings,

    /// <summary>
    /// Nothing FileFluss can ask for again: the account has to be removed and added afresh (a
    /// folder pick, a paired camera).
    /// </summary>
    AddAgain,
}

public static class ReconnectMethodExtensions
{
    /// <summary>
    /// The way back for this kind of account. Drime goes through its form, where macOS opens the
    /// browser once it has a Drime client ID: the Windows port has no browser sign-in for Drime,
    /// so a new token is what reconnects it.
    /// </summary>
    public static ReconnectMethod ReconnectMethod(this CloudProviderType type) => type switch
    {
        CloudProviderType.GoogleDrive or CloudProviderType.Dropbox or CloudProviderType.Box or
            CloudProviderType.OneDrive or CloudProviderType.PCloud => Method.Browser,
        CloudProviderType.ICloud => Method.SystemSettings,
        CloudProviderType.GoogleDrivePicker or CloudProviderType.GoPro => Method.AddAgain,
        _ => Method.Credentials,
    };
}
