# Porting a cloud provider to Windows

Every macOS provider (Swift, `Packages/FileFlussCore/Sources/FileFlussCore/Providers/`) gets a
C# counterpart in `windows/src/FileFluss.Core/Providers/<Name>/`. The Swift code is the
specification: it encodes years of field reports (token rotation races, path-cache eviction,
size-limit quirks). Port behaviour faithfully, including the comments that explain *why* —
condensed, but keep the reasons.

## Shape

```csharp
namespace FileFluss.Core.Providers.Dropbox;

[CloudProvider(CloudProviderType.Dropbox)]
public sealed class DropboxProvider : CloudProvider
{
    // Used by the registry.
    public DropboxProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null) { }

    // Test seam: tests pass a FakeHttpHandler. null → CloudHttp.Create().
    internal DropboxProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials) { ... restore credentials from the store, no network ... }

    public override CloudProviderType ProviderType => CloudProviderType.Dropbox;
    protected override string CredentialKeyPrefix => "dropbox";   // the macOS keychainKey prefix
    public override bool IsAuthenticated => _credentials is not null;
    public override ConnectSpec ConnectSpec => ...;
    public override Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken ct) => ...;
    ... file operations ...
}
```

* Put the API client, JSON/XML models and parsers next to the provider, `internal` unless
  the app needs them. One folder per provider family; families that share code (WebDAV +
  NextCloud + GMX, S3 + S3-compatible + Synology C2) share it inside their folder.
* **Do not edit files outside your folder(s)** and your test files. If the shared contract
  (`FileProvider`, `CloudProvider`, `Connect.cs`, `Net/*`, `IO/*`) is missing something,
  work around it locally and describe the gap in your final report.
* Packages already referenced by Core: SSH.NET, FluentFTP, Microsoft.Data.Sqlite. No others.

## The contract (read these files first)

* `Providers/FileProvider.cs` — operations, path rules, semantics of download/upload.
* `Providers/CloudProvider.cs` — lifecycle, credentials, connect, share links, quota.
* `Providers/Connect.cs` — the Add/Edit Account form schema and `IConnectPrompt`.
* `Providers/CloudProviderException.cs` — the only exception type for expected failures.
* `Net/CloudHttp.cs`, `Net/HttpTransfer.cs`, `Net/OAuth.cs` — HTTP, progress, OAuth loopback + PKCE.
* `IO/CloudPath.cs` (POSIX path helpers, path-cache eviction), `IO/SafeLocalPath.cs`.

## Rules

1. **Paths** are POSIX, root `/`, no trailing slash. Use `CloudPath` helpers.
2. **Async everywhere** with `CancellationToken` threaded through to every network call.
   `ConfigureAwait(false)` in Core.
3. **HTTP**: one `HttpClient` per provider instance from `CloudHttp.Create()` (or from the
   injected handler). API calls via `SendApiAsync` (bounded), transfers via
   `SendTransferAsync` + `HttpTransfer.SaveToFileAsync` (download) and
   `ProgressStreamContent` / `FileSliceStream` (upload, chunked upload). Report bytes through
   the `ByteProgress` delegate as deltas. Never buffer a whole file in memory.
4. **Errors**: throw `CloudProviderException` (`Unauthorized`, `NotFound(path)`,
   `CommandFailed(serverMessage)` …). Surface the server's own message where the Swift code
   does. Map 401 to `Unauthorized()` so the panel shows its re-auth banner.
5. **Credentials** persist through `Credentials.Save(CredentialKey, record)` (JSON, any
   record type) and are restored in the constructor. Never log tokens, passwords, keys or
   response bodies that carry them. `SupportLogger.Shared.Log(...)` for diagnostics.
6. **Token refresh is single-flight**: concurrent callers await one refresh (`SemaphoreSlim`
   or a shared `Task`). Persist refreshed tokens immediately.
7. **Thread safety**: one provider instance serves panels, transfers and the indexer at once.
   Guard mutable state (path→id caches: `ConcurrentDictionary` or a lock). Port the
   macOS `removePathSubtree` / `retainPathChildren` cache maintenance (`PathCacheExtensions`).
8. **Strings** shown to the user go through `Localization.L("English macOS text")`, with Cocoa
   `%@`/`%d` written as `{0}`, `{1}`. Use the macOS text *verbatim* so the existing
   German/Chinese translations apply (grep `FileFluss/Resources/en.lproj/Localizable.strings`
   and `Packages/FileFlussCore/Sources/FileFlussCore/Resources/en.lproj/Localizable.strings`).
   Replace macOS-only wording only where it is wrong on Windows ("Keychain" →
   "Credential Manager", "⌥⌘I" → "F12").
9. **ConnectSpec** mirrors the macOS `AddCloudAccountView.swift` section for the provider
   (fields, help text, links, warnings, modes) and `EditCloudAccountView.swift` for editing.
   `GetEditableConnectValues()` returns the non-secret values to prefill. `ConnectAsync`
   must not overwrite stored credentials unless sign-in succeeded. Use
   `IConnectPrompt.RequestCodeAsync` for a 2FA code the server asks for mid-sign-in,
   `ChooseAsync` for picking among several drives/libraries, `ShowStatus` for
   "Waiting for sign-in in browser…".
10. **Optional capabilities** (`MoveItemAsync`, `CopyItemAsync`, `SetModificationDateAsync`,
    `SearchFilesAsync`, `FolderSizeAsync`, share links, `StorageQuotaAsync`,
    `MaxUploadFileSize`) are implemented exactly where the Swift provider implements them.
11. **Downloads** of exported formats (Google Docs → DOCX etc.) keep the Swift behaviour.

## Tests

`windows/src/FileFluss.Core.Tests/Providers/<Name>Tests.cs`, xUnit. Use
`Support/FakeHttpHandler` to script HTTP and assert on what was sent. Cover at least:
listing parse (incl. dates, sizes, folders), path → request mapping, an upload (bytes
reported, chunking boundaries where applicable), error mapping (401 → Unauthorized,
404 → NotFound), token refresh single-flight, connect happy path + a failure that leaves
stored credentials untouched, share-link and quota parsing where implemented. Port any
existing macOS tests for the provider (`FileFlussTests/*`, `Packages/FileFlussCore/Tests/*`).
Live tests against public demo servers are welcome but must be opt-in
(`[Fact(Skip = "live")]` style or an env var guard) — CI has no network credentials.

Build and test from the `windows` folder:

```
dotnet build FileFluss.sln
dotnet test src/FileFluss.Core.Tests
```

Warnings are errors. Commit on your branch with a subject like
`Windows: Dropbox and Box providers` — no AI attribution or Co-Authored-By lines.
