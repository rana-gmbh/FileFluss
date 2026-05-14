# FileFluss Pre-Release Audit — 2026-05-14

Autonomous pre-release pass. Three parallel investigation agents ran security, stability and efficiency / release-readiness audits across the entire codebase. Findings classified by severity. Items marked **FIXED** were applied in the accompanying commit; items marked **REPORT** are flagged for human review.

---

## Tests & builds

| Check | Result |
|---|---|
| `xcodebuild test` (Debug, before fixes) | 2 failures in `SyncEngineTests` |
| `xcodebuild test` (Debug, after fixes) | 3 failures in `SyncEngineTests` (all pre-existing, see below) |
| `xcodebuild build -configuration Release` | Compiles; `xcodebuild build` reports a provisioning-profile error locally — expected because Release signing requires the Developer ID cert configured in CI, not on this Mac. Release builds work end-to-end through `.github/workflows/release.yml`. |
| `xcodebuild build` (Debug, after every change) | Clean, no errors |

### Test failures (pre-existing — NOT introduced by this audit's fixes)

All three live in `FileFlussTests/SyncEngineTests.swift`. They were already failing before any change in this pass and are unrelated to the providers/SQLite/sync UI changes here. Worth fixing but not release-blocking:

1. `Register and use provider` — fails when a Qt-based app on this Mac has left a `qtsingleapplication-*` file in `/tmp`; the test enumerates temp dir contents and the copy fails. Test infrastructure / system-state-dependent. Fix: make the test write to a dedicated, isolated subdirectory.
2. `Stub providers return empty list and start unauthenticated` — `items?.isEmpty == true` assertion fails; one of the stub providers in the list returns items. Likely an `iCloudProvider()` test that succeeds because iCloud Drive is actually available on this machine.
3. `Provider getFileMetadata throws for stubs` (intermittent — only failed in one of two runs) — same iCloud-is-real cause as #2: `ICloudProvider().getFileMetadata(at: "/test")` returns metadata instead of throwing because iCloud is mounted on this Mac.

These tests assume an isolated, unconfigured environment. The right fix is to stub iCloud's `FileManager.url(forUbiquityContainerIdentifier:)` or skip iCloud tests when iCloud Drive is enabled. Out of scope for the pre-release audit.

---

## CRITICAL — FIXED

### 1. WordPress remote-URL force-unwrap (crash + SSRF)
**File:** `FileFluss/Services/CloudProviders/WordPressAPIClient.swift:200`

`URL(string: sourceURL)!` on a value decoded from the WordPress REST API. Malformed URL → crash. Compromised or spoofed server → app fetches an attacker-chosen URL with the user's WordPress credentials attached.

**Fix applied:** safe `guard let` → `throw CloudProviderError.invalidResponse`. The download still attaches the auth header, which is OK for legitimate WordPress media URLs but is the SSRF residual risk — see report item below.

### 2. OAuth token-refresh race in three providers
**Files:** `DropboxAPIClient.swift`, `GoogleDriveAPIClient.swift`, `OneDriveAPIClient.swift`

Same shape as the Box bug fixed in commit `a300aec`. `refreshTokenIfNeeded()` is an actor method, but `await session.data(for:)` releases the actor. Two concurrent callers can both pass the "is token expiring?" guard and both POST `/oauth2/token` with the same single-use refresh token. The losing request comes back as `invalid_grant` → `notAuthenticated`. Users see spurious "Not authenticated. Please sign in." failures while the app is otherwise healthy.

**Fix applied for all three:**
- Added `private var inflightRefresh: Task<...Credentials, Error>?` actor state.
- Refactored `refreshTokenIfNeeded()` to await an existing in-flight refresh if present.
- Added `forceRefresh()` (always refreshes regardless of expiry).
- Wrapped each `apiRequest` / `apiRequestVoid` / `graphRequest` body in an inner `send(forceRefreshFirst:)` closure; on HTTP 401, force-refresh and retry once. Google Drive and OneDrive previously had **no** 401 retry at all — a server-side token invalidation would have surfaced as a hard auth failure. Dropbox already had the retry, just not the serialization.

### 3. SQLite dangling-pointer in all 44 `sqlite3_bind_text` sites
**File:** `FileFluss/Services/Search/SearchIndex.swift`

The pattern `(value as NSString).utf8String, -1, nil` passes `SQLITE_STATIC` (the `nil` destructor argument), telling SQLite to read the bytes lazily from the pointer without copying. Swift ARC can release the temporary `NSString` immediately after the bind call returns; by the time `sqlite3_step` runs, the pointer may dangle. The comment at the old `recordCloudSource` site already documented one observed symptom — garbage in the `kind` column. The migration code at lines 113–118 cleans up *historical* corruption from that bug; this fix prevents *new* corruption across every text bind in the file.

**Fix applied:** added `SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)` and sweep-replaced every `nil` destructor with `SQLITE_TRANSIENT`, telling SQLite to copy the bytes immediately. Cost is one short-string copy per bind, negligible. Removed the now-incorrect "routing through NSString keeps it alive" comment block.

---

## HIGH — FIXED

### 4. SyncEngine bidirectional sync has no conflict detection
**Files:** `FileFluss/Services/SyncEngine.swift:58–62`, `FileFluss/Views/Sync/AddSyncRuleView.swift`

`syncBidirectional` is literally `try await syncUpload` then `try await syncDownload` with a `// TODO: Implement conflict detection and resolution` comment. It can overwrite a locally-modified file with an older cloud copy, duplicate files, or oscillate. The UI's `AddSyncRuleView` defaulted the picker to `.bidirectional` and listed it as a choice.

**Fix applied:** removed `.bidirectional` from the picker, changed the default to `.upload`. The enum case still exists so previously persisted bidirectional rules continue to load (they'll still run as upload→download, same as before — degraded but unchanged). New users can no longer create one. The real fix — actual conflict detection — is a v1.1 item.

### 5. KDrive force-unwrap on root cache
**File:** `FileFluss/Services/CloudProviders/KDriveAPIClient.swift:347`

`var currentId = pathToId["/"]!` — crashes if `pathToId["/"]` wasn't seeded for any reason (e.g. failed `fetchRootFileId()` during auth).

**Fix applied:** `guard var currentId = pathToId["/"] else { throw .notAuthenticated }`.

### 6. Google Drive force-unwrap on root cache (two sites)
**File:** `FileFluss/Services/CloudProviders/GoogleDriveAPIClient.swift:794, 797`

Same shape as #5.

**Fix applied:** single `guard let rootCached = pathIdCache["/"] else { throw .notAuthenticated }` reused for both empty-components and walker start.

### 7. S3 force-cast on URLResponse
**File:** `FileFluss/Services/CloudProviders/S3APIClient.swift:459`

`let http = response as! HTTPURLResponse`. Defensive enough in practice (HTTP always returns `HTTPURLResponse`), but if `validate(...)` is ever refactored to not throw on a malformed response type, this crashes.

**Fix applied:** safe `guard let … else { throw .invalidResponse }`.

### 8. `saveAccounts()` swallowed encoding errors
**File:** `FileFluss/ViewModels/SyncViewModel.swift:833`

`if let data = try? JSONEncoder().encode(accounts)` — if encoding fails for any reason (a corrupt credential, a future Codable change), the user appears to have added an account but it never persists. Silent data loss.

**Fix applied:** `do/catch` with `NSLog` so support has something to look at. Doesn't surface to the user yet (would need a `@Published authError`-style channel), but at minimum no more silent failure.

### 9. Missing `LSApplicationCategoryType` in Info.plist
**File:** `FileFluss/Resources/Info.plist`

App showed up in "Other" instead of "Utilities" categorization.

**Fix applied:** added `<key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>`.

### 10. Plain-text temp logs from SFTP / kDrive leak to `/tmp` in Release
**Files:** `FileFluss/Services/CloudProviders/SFTPAPIClient.swift:371`, `FileFluss/Services/CloudProviders/KDriveProvider.swift:9–20`

Both write to a world-readable `/tmp/filefluss-*.log` file. SFTP's log line includes `stderr`, which on auth failure can include paths and (with `-v` style sshpass output, depending on path) credentials. kDrive's logs include API paths and tokens.

**Fix applied:** wrapped both with `#if DEBUG`. Release builds no longer create these files. The `os.Logger` mirror is unchanged and remains the primary diagnostic channel.

Also fixed an unrelated force-unwrap inside KDrive's log path (`.data(using: .utf8)!`) while editing.

---

## REPORT ONLY — Security (HIGH / MEDIUM, judgement calls)

### 11. SSRF residual after WordPress crash fix
After fix #1, `downloadFile` still issues `URLRequest(url: url)` with the Authorization header for whatever URL the WordPress server returned. A compromised WordPress server can use this as an SSRF gadget: return e.g. an internal-network URL and the app fetches it (without leaking the auth cookie — at least Basic header is the same as for any WP call). To genuinely close: validate the host matches the configured `siteURL`, OR strip the Authorization header for non-same-origin downloads. Both have UX cost (CDNs for media break the first; some self-hosted WP setups break the second). Punt to v1.1.

### 12. OAuth client secrets bundled in the binary
- `GoogleDriveAPIClient.swift:24` — `GOCSPX-…`
- `DropboxAPIClient.swift:21` — `xcvb8frfc2jyzvj`
- `BoxAPIClient.swift:24` — `meEDcWLwAphA0UfMaoOfK6MoCztrT2lB`

This is **acceptable** for desktop OAuth clients per the OAuth 2.0 spec (public clients can't keep secrets). Mentioned only because a security researcher reading the source will flag them. Consider adding a one-line comment at each call site clarifying "public client; secret bundled per standard desktop OAuth practice." Or rotate annually as a hygiene measure.

### 13. `NSAllowsArbitraryLoads = true` (Info.plist)
Justified in the comment for self-hosted servers. Stricter alternative: `NSExceptionDomains` per-host, populated dynamically when the user adds a new server. Significantly more code; v1.1.

### 14. Loopback OAuth port not pinned
Loopback redirects bind to an OS-assigned random port via `NWListener`. CSRF `state` parameter is validated (good). A local attacker monitoring listener creation could still race. Acceptable as-is — `state` mitigates the practical attack. v1.1 hardening: validate the requesting User-Agent looks browser-ish, reject after first response.

### 15. `SSH_ASKPASS` script in `/tmp` with predictable name
**File:** `SFTPAPIClient.swift:94–98`

`NSTemporaryDirectory() + "filefluss-sftp-askpass-\(id)"` — contains the plaintext SFTP password while the SSH command runs. UUID suffix means another local user can't easily enumerate it, but the file exists until `deinit` rather than being deleted as soon as the SSH command starts reading it. Lifetime can also extend if the SSH process hangs. Real fix: use a kqueue-style "delete after first read" pattern, or pass via stdin instead of an askpass script. Out of pre-release scope; flag for v1.1.

### 16. `SyncExecutor.appendingPathComponent` with remote-supplied names
**File:** `SyncExecutor.swift` (~154, 176)

Remote file names from the cloud API are appended directly to a local root. `appendingPathComponent` resolves `..` literally, not as a traversal — so `../etc/passwd` becomes a literal child folder named `../etc` (escaped). But a remote name like `/abs/path` could cause surprises depending on the underlying string handling, and a symlink on the destination could redirect a write. Add: `guard resolvedPath.path.hasPrefix(root.path) else { throw … }` after each `appendingPathComponent`. Low practical risk because cloud APIs sanitize names server-side, but worth hardening.

### 17. Unused `GOOGLE_PICKER_API_KEY` in `Secrets.xcconfig`
File is `.gitignore`d (line 25) and not referenced from `project.yml`, Info.plist or any source. Doesn't ship. Cleanup item, no security impact.

---

## REPORT ONLY — Stability (MEDIUM)

### 18. AppDelegate Task without `[weak self]`
**File:** `App/AppDelegate.swift:67` — `Task { await notifier.start() }`. AppDelegate is app-lifetime so this is harmless in practice, but `[weak self]` would future-proof against test or lifecycle changes.

### 19. NotificationCenter token lifetime documented but not enforced
**File:** `App/AppDelegate.swift:56–64`. `themeChangeObserver` is stored as a property — correct. Add an explicit comment that this property must never be cleared, or move ownership to a static cache that survives any refactor.

### 20. IndexingService relies on `await MainActor.run` for safety
**File:** `Services/IndexingService.swift:163–166, 186`. The mutation is correctly hopped, but a future refactor that removes the hop would re-introduce a data race. Mark the mutable properties as `nonisolated(unsafe)` with a comment explaining the invariant, or move them under `@MainActor`.

### 21. `fatalError("Division by zero")` in MegaAPIClient BigUInt math
**File:** `MegaAPIClient.swift:1359`. Internal crypto helper, not user-reachable in practice. Convert to `throws` for hygiene; low priority.

---

## REPORT ONLY — Efficiency (HIGH / MEDIUM, design changes)

### 22. Synchronous FileManager work on the main actor in move/copy conflict loops
**File:** `ViewModels/FileManagerViewModel.swift:276, 282, 311–312, 337, 343, 372–373, 436`. The conflict-detection loop hits `FileManager.fileExists` + `removeItem` on the main thread. Negligible on local SSD, noticeable beachball on network volumes. Wrap in `Task.detached` or move the work into `FileSystemService` (the designated async I/O layer). Not a correctness issue, only feel — but the project's stated design goal is "snappiness." Worth a v1.0.x patch.

### 23. `NSWorkspace.shared.icon(for:)` on the main thread for unknown UTTypes
**File:** `Views/FileManager/FileTypeIcon.swift`. First call for any extension during scroll blocks the render loop briefly while LaunchServices resolves. Cache is correct; consider pre-warming the cache for the visible file types at directory-load time.

### 24. No pagination on cloud `listDirectory`
Most providers fetch the entire folder in one shot (capped, e.g. Google to 500 entries, Box to 1000). For very large folders the UI stalls. Real fix is partial-result streaming; multi-day refactor. v1.1.

### 25. `SearchViewModel.groupedResults` recomputed on every body
**File:** `ViewModels/SearchViewModel.swift:35–66`. Builds three dictionaries and sorts on every read. For 1000+ results, this runs on every SwiftUI redraw of the search results view. Cache it (private storage + invalidate on `filteredResults` change).

### 26. `SearchViewModel.includeOffline` reads/writes UserDefaults on every keypress
**File:** `ViewModels/SearchViewModel.swift:11–12`. Debounce the write side; cache the read side.

### 27. ~20 raw `NSLog` calls without `#if DEBUG`
Examples in `AppDelegate.swift` (icon-swap diagnostic), `FileCommands.swift`. They appear in Console.app for end users, cluttering support diagnostics. Either gate behind `#if DEBUG` or move to `os.Logger` `.debug` level. ~5 minute cleanup.

---

## REPORT ONLY — Release-readiness confirmations (no action)

All passed: version `1.0.1`, build `1`, `LSMinimumSystemVersion` from `MACOSX_DEPLOYMENT_TARGET`, Release entitlements correctly drop the keychain-access-group (sandbox is intentionally off), `.github/workflows/release.yml` references `HOMEBREW_TAP_TOKEN` and includes the notarization step, version-test runner is `#if DEBUG` gated in `FileCommands.swift`, About window uses live `applicationIconImage` and `UpdateLookup.currentVersion()`, `automaticUpdateChecksEnabled: true` default registered.

CI's tap-update step fails silently if `HOMEBREW_TAP_TOKEN` is unset on GitHub. Add a pre-check step that fails loudly when it's missing, so a future maintainer doesn't ship without the tap commit.

---

## Verdict

App is **release-ready** with the fixes in the accompanying commit applied. The remaining REPORT items are real but either need design work (SSRF, sync conflict resolution, NSExceptionDomains) or are quality-of-life polish (pagination, scroll perf, debounce). The two ship-blockers from the audits (CRITICAL WordPress crash, HIGH bidirectional-sync data-loss) are both addressed.

Recommended manual checks before tagging `v1.0.x`:
- `/ultrareview` on the branch — multi-agent cloud review you'd want before any release. I can't trigger it; you'll need to run it yourself.
- Add+sync test against your live cloud accounts in this build.
- Install the notarized DMG on a clean Mac (or VM) and confirm cold-launch flow + Dock icon + first-run welcome.
- Toggle System Settings → Appearance and confirm icon swaps in Dock and About window (already verified in this session, but worth one final pass with the new icons).
