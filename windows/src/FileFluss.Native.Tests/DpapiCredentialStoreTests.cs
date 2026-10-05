// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FileFluss.Native.Tests;

public sealed class DpapiCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ff-credentials-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void SecretsRoundTripAndAreNotStoredInTheClear()
    {
        var store = new DpapiCredentialStore(_directory);
        var secret = Encoding.UTF8.GetBytes("""{"refreshToken":"very-secret-refresh-token"}""");

        store.Save("dropbox.0000", secret);

        Assert.Equal(secret, store.Load("dropbox.0000"));
        var file = Assert.Single(Directory.GetFiles(_directory));
        Assert.DoesNotContain("very-secret", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        Assert.DoesNotContain("dropbox", Path.GetFileName(file), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptOrTruncatedDataReadsAsAbsent()
    {
        var store = new DpapiCredentialStore(_directory);
        store.Save("box.1", [1, 2, 3]);
        var file = Assert.Single(Directory.GetFiles(_directory));

        File.WriteAllBytes(file, [0xDE, 0xAD]);
        Assert.Null(store.Load("box.1"));

        File.WriteAllBytes(file, []);
        Assert.Null(store.Load("box.1"));
    }

    [Fact]
    public void OverwritingLeavesNoTemporaryFiles()
    {
        var store = new DpapiCredentialStore(_directory);
        store.Save("k", [1]);
        store.Save("k", [2]);
        store.Delete("k");
        store.Delete("k");

        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Null(store.Load("k"));
    }

    [Fact]
    public void TheFolderIsPrivateToThisUser()
    {
        new DpapiCredentialStore(_directory).Save("k", [1]);

        var security = new DirectoryInfo(_directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();
        Assert.Equal(
            new HashSet<SecurityIdentifier> { WindowsIdentity.GetCurrent().User!, new(WellKnownSidType.LocalSystemSid, null) },
            allowed);

        // The secret file inherits it.
        var file = Assert.Single(Directory.GetFiles(_directory));
        var fileRules = new FileInfo(file).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), fileRules);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), fileRules);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), fileRules);
    }
}
