// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Security;

namespace FileFluss.Core.Tests.Security;

/// <summary>
/// macOS: a keychain read that fails for any reason but a missing item goes to the support log,
/// so credentials that are saved but unreadable can be told apart from deleted ones.
/// </summary>
public sealed class CredentialStoreLoggingTests
{
    public sealed record StoredToken(string Value);

    [Fact]
    public void SavedCredentialsThatNoLongerDecodeAreLoggedWithoutWhatTheyHold()
    {
        var store = new MemoryCredentialStore();
        var key = "webdav." + Guid.NewGuid().ToString("N");
        var missing = "webdav." + Guid.NewGuid().ToString("N");
        store.Save(key, Encoding.UTF8.GetBytes("""{"value":["secret-in-the-wrong-shape"]}"""));
        var since = DateTimeOffset.Now.AddSeconds(-1);

        Assert.Null(store.Load<StoredToken>(key));
        Assert.Null(store.Load<StoredToken>(missing));

        var entry = Assert.Single(SupportLogger.Shared.Snapshot(since), e => e.Message.Contains(key, StringComparison.Ordinal));
        Assert.Equal((LogLevel.Error, CredentialStoreExtensions.LogCategory), (entry.Level, entry.Category));
        Assert.Contains("$.value", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal);

        // Nothing saved is ordinary, and not worth a line.
        Assert.DoesNotContain(SupportLogger.Shared.Snapshot(since), e => e.Message.Contains(missing, StringComparison.Ordinal));
    }
}
