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

**MVVM + Observable** — SwiftUI app targeting macOS 14.0, Swift 6.0.

### State flow

`AppState` (global `@Observable`) → owns two `FileManagerViewModel` instances (left/right panels) + one `SyncViewModel`. Injected into views via SwiftUI environment.

### Layers

- **App/** — Entry point (`FileFlussApp`) and `AppState`
- **Models/** — `FileItem`, `CloudAccount`, `SyncRule` (plain data types)
- **Services/** — Actor-based: `FileSystemService` (local FS ops), `SyncEngine` (cloud sync orchestration), `CloudProvider` protocol + 5 stub providers
- **ViewModels/** — `@Observable @MainActor` view models coordinating between views and services
- **Views/** — SwiftUI views; `NativeFileList` bridges to AppKit `NSTableView` via `NSViewRepresentable`

### Key patterns

- **Concurrency**: Actors for services, `@MainActor` for view models/views, `async/await` throughout
- **Dual-panel UI**: Left and right file manager panels tracked by `AppState.activePanel`
- **Cloud providers**: Protocol-based (`CloudProvider`), implementations are stubs with TODOs
- **App Sandbox disabled** — app uses direct file system access with security-scoped bookmarks

### No external dependencies

Uses only Apple frameworks (SwiftUI, AppKit, Foundation, QuickLookUI, UniformTypeIdentifiers).

## Commits

Never mention Claude or AI in commit messages or Co-Authored-By lines. All commits are authored by Robert Rudolph from Rana GmbH.

## Releases

Releases are triggered by pushing a `v*` tag. `.github/workflows/release.yml` builds, notarizes, and publishes the DMG, then computes its SHA-256 and pushes a cask bump to the **separate tap repo** `rana-gmbh/homebrew-filefluss` (this is the repo Homebrew actually reads when users run `brew tap rana-gmbh/filefluss`). The cask file does **not** live in this repo.

The workflow's tap-update step needs a secret named `HOMEBREW_TAP_TOKEN` — a fine-grained PAT scoped to `rana-gmbh/homebrew-filefluss` with `contents: write`. Without it the step fails; with it, every tagged release lands a matching cask commit automatically.

Never set `sha256 :no_check` in the tap's cask. Homebrew warns on every install when verification is skipped, and the cask URL is pinned per-version so a real hash is required. If the tap ever drifts from the published DMG (e.g. a manual release), regenerate the hash and update both `version` and `sha256` in `rana-gmbh/homebrew-filefluss`:

```bash
VERSION=0.8.1
curl -fL "https://github.com/rana-gmbh/filefluss/releases/download/v${VERSION}/FileFluss-v${VERSION}.dmg" | shasum -a 256
```

## Version test (debug builds only)

Debug builds must expose the **Run Version Test…** menu item (File → Support Log submenu), which invokes `VersionTestRunner` to exercise every connected cloud account and write a report to `Testfiles/`. Release builds must **not** show this option — gate it with `#if DEBUG` in `FileFluss/App/FileCommands.swift`. Never ship the version test runner to end users.

## Design Philosophy

Speed and snappiness are top priorities. Prefer lazy loading, background processing, and non-blocking UI patterns. Avoid synchronous work on the main thread. Every interaction should feel instant.
