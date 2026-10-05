// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.AccessControl;
using System.Security.Principal;

namespace FileFluss.Native;

/// <summary>
/// Folders FileFluss keeps to itself — the credential store, the download cache — readable
/// and writable by the signed-in user (and SYSTEM) only, with inheritance from the parent
/// cut. %LOCALAPPDATA% and %TEMP% are private already; a cache folder the user moved to a
/// second drive is not (the root of a data drive lets every signed-in user create and change
/// files), and there another account could swap a downloaded file before it is opened.
/// </summary>
public static class PrivateFolder
{
    /// <summary>
    /// Restricts <paramref name="path"/> (and, by inheritance, everything in it). Skipped when
    /// the folder is already cut off from its parent, so a large cache isn't re-walked on
    /// every start. Best effort: false when the permissions couldn't be read or changed.
    /// </summary>
    public static bool Apply(string path)
    {
        try
        {
            var folder = new DirectoryInfo(path);
            if (folder.GetAccessControl().AreAccessRulesProtected)
            {
                return true;
            }

            folder.SetAccessControl(Security());
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException
                                      or PrivilegeNotHeldException or IdentityNotMappedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Full control for the current user and SYSTEM, inherited by everything inside; nothing else, nothing inherited from above.</summary>
    internal static DirectorySecurity Security()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var identity in new IdentityReference[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}
