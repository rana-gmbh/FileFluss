# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

Project uses **XcodeGen** to generate the Xcode project from `project.yml`.

```bash
# Regenerate Xcode project after changing project.yml
xcodegen generate

# Build
xcodebuild build -project FileFluss.xcodeproj -scheme FileFluss -destination 'platform=macOS'

# Run all tests
xcodebuild test -project FileFluss.xcodeproj -scheme FileFluss -destination 'platform=macOS'

# Run a single test (by name)
xcodebuild test -project FileFluss.xcodeproj -scheme FileFluss -destination 'platform=macOS' -only-testing:FileFlussTests/FileItemTests
```

Tests use Swift Testing framework (`@Suite`, `@Test` macros), not XCTest.

## Architecture

**MVVM + Observable** — SwiftUI app targeting macOS 14.0, Swift 6.3.

### State flow

`AppState` (global `@Observable`) → owns two `FileManagerViewModel` instances (left/right panels) + one `SyncViewModel`. Injected into views via SwiftUI environment.

### Layers

- **App/** — Entry point (`FileFlussApp`) and `AppState`
- **Models/** — `FileItem`, `CloudAccount`, `SyncRule` (plain data types)
- **Services/** — Actor-based: `FileSystemService` (local FS ops), `SyncEngine` (cloud sync orchestration), `CloudProvider` protocol + 18 concrete providers: Dropbox, Google Drive, OneDrive, Box, pCloud, kDrive, Koofr, Mega, S3, S3-compatible, NextCloud, WebDAV, SFTP, Synology C2, Synology Drive, iCloud, GMX, WordPress
- **ViewModels/** — `@Observable @MainActor` view models coordinating between views and services
- **Views/** — SwiftUI views; `NativeFileList` bridges to AppKit `NSTableView` via `NSViewRepresentable`

### Key patterns

- **Concurrency**: Actors for services, `@MainActor` for view models/views, `async/await` throughout
- **Dual-panel UI**: Left and right file manager panels tracked by `AppState.activePanel`
- **Cloud providers**: Protocol-based (`CloudProvider`), with full implementations for each supported service (see Services/CloudProviders/)
- **App Sandbox disabled** — app uses direct file system access with security-scoped bookmarks

### Dependencies

Apple frameworks (SwiftUI, AppKit, Foundation, QuickLookUI, UniformTypeIdentifiers) plus **one** third-party package: [Sparkle](https://github.com/sparkle-project/Sparkle), pinned to an exact version in `project.yml`, for in-app updates. Keep it that way — anything else should be justified before it's added.

## Commits

Never mention Claude or AI in commit messages or Co-Authored-By lines. All commits are authored by Robert Rudolph from Rana GmbH.

## Releases

**The Mac app and the Windows app are released together, from one tag.** Pushing `vX.Y.Z` (or `vX.Y.Z-beta.N`, `-alpha.N`, `-rc.N`; anything else is refused) runs `.github/workflows/release.yml`: the Mac job builds, notarizes and packages the DMG plus Sparkle's ZIP and `appcast.xml`, the Windows job tests, builds and signs the installers and portable zips, and only when both succeed does the publish job create **one** GitHub release with all of it and a single `SHA256SUMS.txt`. It is created as a draft and published once complete, so no one — Sparkle included — ever sees half a release. If one platform fails, nothing is published; fix it and re-run the failed jobs. A hyphenated version is a pre-release: not "latest", no tap bump.

`RELEASE_NOTES.md` is the release body (and what Sparkle shows), so it covers both apps; say which platform a feature is on when they differ. Always three version numbers: the Windows build stamps them into its executables.

The publish job then computes the DMG's SHA-256 and pushes a cask bump to the **separate tap repo** `rana-gmbh/homebrew-filefluss` (this is the repo Homebrew actually reads when users run `brew tap rana-gmbh/filefluss`). The cask file does **not** live in this repo.

The Windows job signs through the `windows-release` environment, whose tag rules must allow `v*` (and `win-v*` while `windows-release.yml` is still around).

The workflow's tap-update step needs a secret named `HOMEBREW_TAP_TOKEN` — a fine-grained PAT scoped to `rana-gmbh/homebrew-filefluss` with `contents: write`. Without it the step fails; with it, every tagged release lands a matching cask commit automatically.

Never set `sha256 :no_check` in the tap's cask. Homebrew warns on every install when verification is skipped, and the cask URL is pinned per-version so a real hash is required. If the tap ever drifts from the published DMG (e.g. a manual release), regenerate the hash and update both `version` and `sha256` in `rana-gmbh/homebrew-filefluss`:

```bash
VERSION=0.8.1
curl -fL "https://github.com/rana-gmbh/filefluss/releases/download/v${VERSION}/FileFluss-v${VERSION}.dmg" | shasum -a 256
```

## In-app updates (Sparkle)

`AppUpdater` (`FileFluss/Services/AppUpdater.swift`) drives Sparkle 2. Three Info.plist keys matter: `SUFeedURL` (the `appcast.xml` attached to the latest GitHub release), `SUPublicEDKey` (the public half of the release signing key) and `SUEnableAutomaticChecks`. The user-facing toggle is Settings → "Check for updates automatically", stored as `automaticUpdateChecksEnabled`; Sparkle reads the same key.

**`AppUpdater` refuses to start when `SUPublicEDKey` is empty**, which is deliberate: a build that can't verify a signature must not download and install one. "Check for Updates" then opens the releases page instead.

Releases are signed with an EdDSA keypair. The private half lives **only** in the GitHub secret `SPARKLE_PRIVATE_KEY` (never in the repo); the public half sits in Info.plist. `release.yml` downloads Sparkle's pinned, checksummed tooling, generates `appcast.xml`, and refuses to publish an unsigned feed or one pointing at the wrong archive. `verify-sparkle-key.yml` (manual trigger) proves the secret and the public key are still a pair — run it after any key change.

Sparkle updates from the **ZIP**, notarized and stapled, built from the same exported app as the DMG. The DMG remains the artifact humans download. `CFBundleVersion` is stamped in CI from the commit count and is what Sparkle compares, so the release checkout must stay `fetch-depth: 0` — a shallow clone counts 1 every time and nobody would ever be offered an update.

The Homebrew cask carries `auto_updates true` so Homebrew doesn't fight Sparkle over the version it thinks is installed.

## Version test (debug builds only)

Debug builds must expose the **Run Version Test…** menu item (File → Support Log submenu), which invokes `VersionTestRunner` to exercise every connected cloud account and write a report to `Testfiles/`. Release builds must **not** show this option — gate it with `#if DEBUG` in `FileFluss/App/FileCommands.swift`. Never ship the version test runner to end users.

## Design Philosophy

Speed and snappiness are top priorities. Prefer lazy loading, background processing, and non-blocking UI patterns. Avoid synchronous work on the main thread. Every interaction should feel instant.

## Windows port

The Windows port lives in `windows/` (C#, .NET 10, WPF) — see `windows/README.md`. The macOS Swift code is its specification, and the macOS `Localizable.strings` files (both locations) are the single source of truth for its strings.

```bash
dotnet build windows/FileFluss.sln                                      # warnings are errors
dotnet test windows/src/FileFluss.Core.Tests
dotnet run --project windows/src/FileFluss.App
dotnet run --project windows/tools/StringsToResx                        # after editing any Localizable.strings
powershell -File windows/Packaging/build-release.ps1 -Version 1.0.0     # installers, portable zips, SHA256SUMS.txt
```

**Windows is released together with the Mac, under the same `vX.Y.Z` tag** — see Releases above. The Windows in-app updater (`UpdateLookup` / `UpdateService`) offers `v*` releases that carry Windows files (older `v*` releases are the Mac's alone and are skipped) as well as the older Windows-only `win-v*` ones, and installs nothing it cannot match against the release's `SHA256SUMS.txt`.

`windows-release.yml` (`win-v*` tags) is kept for one thing only: a last Windows-only beta that carries the updater which understands `v*` releases to the betas installed before the joint releases, which look at `win-v*` releases alone. A `win-v*` release must be created with `--latest=false` — the macOS Sparkle feed is `releases/latest/download/appcast.xml` — and that workflow does so. Delete it, and the `win-v*` rule on the `windows-release` environment, once that beta has shipped.

Commits to the Windows port follow the same rule as everywhere else: never mention Claude or AI in commit messages or Co-Authored-By lines. All commits are authored by Robert Rudolph from Rana GmbH.
